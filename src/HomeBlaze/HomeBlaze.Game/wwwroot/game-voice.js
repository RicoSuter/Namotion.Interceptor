import { getVoiceKeyMethod } from './game-input.js';

// The pages of a tab share this module, so each one owns its voice by the id it passes.
const voices = new Map();
const WORKLET_URL = new URL('./game-voice-worklet.js', import.meta.url).href;
const VOICE_BUTTONS = '[data-game-voice]';
const STARTED = { isStarted: true, error: null };
const NOT_STARTED = { isStarted: false, error: 'failed' };
const IS_LITTLE_ENDIAN = new Uint8Array(new Uint16Array([1]).buffer)[0] === 1;

/**
 * Encodes the 16-bit samples of a frame as little-endian bytes; the one place in JavaScript to change for another codec.
 * The result may share the buffer of the samples, which the caller must not change afterwards.
 */
export function encodeFrame(samples) {
    if (IS_LITTLE_ENDIAN) {
        return new Uint8Array(samples.buffer, samples.byteOffset, samples.byteLength);
    }

    const bytes = new Uint8Array(samples.length * 2);
    const view = new DataView(bytes.buffer);
    for (let index = 0; index < samples.length; index++) {
        view.setInt16(index * 2, samples[index], true);
    }

    return bytes;
}

/** Decodes the little-endian bytes of a frame into samples from -1 to 1. */
export function decodeFrame(frame) {
    const view = new DataView(frame.buffer, frame.byteOffset, frame.byteLength);
    const samples = new Float32Array(frame.byteLength >> 1);
    for (let index = 0; index < samples.length; index++) {
        samples[index] = view.getInt16(index * 2, true) / 32768;
    }

    return samples;
}

/**
 * Prepares the voice of the view; returns false where the browser offers no microphone or audio worklet, as on plain
 * HTTP. Clicks on the elements with data-game-voice inside options.element and the voice keys unlock the audio.
 */
export function initialize(viewId, dotNet, options) {
    dispose(viewId);
    const isSupported = window.isSecureContext
        && typeof navigator.mediaDevices?.getUserMedia === 'function'
        && typeof AudioWorkletNode === 'function';
    if (isSupported) {
        voices.set(viewId, new GameVoice(dotNet, options));
    }

    return isSupported;
}

/** Asks for the microphone and starts sending frames; returns { isStarted, error } with error 'denied', 'not-found' or 'failed'. */
export async function startCapture(viewId) {
    const voice = voices.get(viewId);
    return voice ? await voice.startCapture() : NOT_STARTED;
}

export function stopCapture(viewId) {
    voices.get(viewId)?.stopCapture();
}

/** Starts playing the frames that play() passes; returns { isStarted, error }. */
export async function startPlayback(viewId) {
    const voice = voices.get(viewId);
    return voice ? await voice.startPlayback() : NOT_STARTED;
}

export function stopPlayback(viewId) {
    voices.get(viewId)?.stopPlayback();
}

/** Resumes a suspended audio context; strict browsers allow it only inside a click, which unlock() covers. */
export function resume(viewId) {
    voices.get(viewId)?.resume();
}

export function play(viewId, frame) {
    voices.get(viewId)?.play(frame);
}

/** Stops capture and playback, releases the microphone and closes the audio context of the view. */
export function dispose(viewId) {
    const voice = voices.get(viewId);
    if (voice) {
        voices.delete(viewId);
        voice.dispose();
    }
}

function toError(error) {
    switch (error?.name) {
        case 'NotAllowedError':
        case 'SecurityError':
            return 'denied';
        case 'NotFoundError':
        case 'OverconstrainedError':
            return 'not-found';
        default:
            return 'failed';
    }
}

function stopTracks(stream) {
    for (const track of stream.getTracks()) {
        track.stop();
    }
}

class GameVoice {
    constructor(dotNet, options) {
        this.dotNet = dotNet;
        this.options = options;
        this.root = options.element instanceof Element ? options.element : null;
        this.context = null;
        this.workletLoading = null;
        this.capture = null;
        this.playback = null;
        // Increased by every stop, so a start that a stop overtook while it waited gives up.
        this.captureGeneration = 0;
        this.playbackGeneration = 0;
        this.pendingStarts = 0;
        this.sentFrames = 0;
        this.receivedFrames = 0;
        this.underruns = 0;
        this.trims = 0;
        this.isDisposed = false;
        this.onClick = event => {
            if (event.target instanceof Element && event.target.closest(VOICE_BUTTONS)) {
                this.tryUnlock();
            }
        };
        this.onKeyDown = event => {
            if (getVoiceKeyMethod(event)) {
                this.tryUnlock();
            }
        };
        // Capture phase, so the audio unlocks inside the user's action before the page hears of it.
        this.root?.addEventListener('click', this.onClick, true);
        window.addEventListener('keydown', this.onKeyDown, true);
    }

    // Strict browsers such as Safari start and resume audio only synchronously inside a user action, while the page
    // asks for it only after a round trip to the server.
    unlock() {
        if (this.isDisposed) {
            return;
        }

        if (!this.context) {
            this.context = new AudioContext();
            this.context.onstatechange = () => this.reportState();
        }

        this.resume();
    }

    tryUnlock() {
        try {
            this.unlock();
        } catch {
            // The start reports the failure when the page asks for it.
        }
    }

    async ensureContext() {
        if (this.isDisposed) {
            throw new Error('The voice was disposed.');
        }

        this.unlock();
        const context = this.context;
        this.workletLoading ??= context.audioWorklet.addModule(WORKLET_URL);
        try {
            await this.workletLoading;
        } catch (error) {
            this.workletLoading = null;
            throw error;
        }

        if (this.isDisposed) {
            throw new Error('The voice was disposed while it started.');
        }

        return context;
    }

    resume() {
        const context = this.context;
        // Not skipped while running: a suspend still in flight reads as running, and only a later resume overrides it.
        if (context && context.state !== 'closed') {
            context.resume().then(() => this.reportState(), () => this.reportState());
        }
    }

    // An idle context would keep the audio device busy.
    suspendWhenIdle() {
        const context = this.context;
        if (!this.capture && !this.playback && this.pendingStarts === 0 && context?.state === 'running') {
            context.suspend().catch(() => { });
        }
    }

    reportState() {
        const state = this.context?.state;
        if (!this.isDisposed && state && state !== 'closed') {
            this.dotNet.invokeMethodAsync('OnAudioStateChanged', state !== 'running').catch(() => { });
        }
    }

    async startCapture() {
        this.pendingStarts++;
        try {
            return await this.tryStartCapture();
        } finally {
            this.pendingStarts--;
            this.suspendWhenIdle();
        }
    }

    async tryStartCapture() {
        const generation = this.captureGeneration;
        let stream = null;
        try {
            const context = await this.ensureContext();
            stream = await navigator.mediaDevices.getUserMedia({
                audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true },
            });
            if (this.isDisposed || generation !== this.captureGeneration) {
                stopTracks(stream);
                return NOT_STARTED;
            }

            this.removeCapture();
            const source = context.createMediaStreamSource(stream);
            const node = new AudioWorkletNode(context, 'game-voice-capture', {
                numberOfInputs: 1,
                numberOfOutputs: 1,
                processorOptions: { sampleRate: this.options.sampleRate, frameSamples: this.options.frameSamples },
            });
            node.port.onmessage = event => this.send(event.data);
            source.connect(node);
            // The processor outputs only silence; the connection keeps the browser running it.
            node.connect(context.destination);
            this.capture = { stream, source, node };
            this.watchCaptureEnd(stream, generation);
            return STARTED;
        } catch (error) {
            if (stream) {
                stopTracks(stream);
            }

            return { isStarted: false, error: toError(error) };
        }
    }

    // A track ends without stop() when the device is unplugged or the browser revokes access.
    watchCaptureEnd(stream, generation) {
        for (const track of stream.getAudioTracks()) {
            track.onended = () => {
                if (!this.isDisposed && generation === this.captureGeneration) {
                    this.dotNet.invokeMethodAsync('OnCaptureEnded').catch(() => { });
                }
            };
        }
    }

    send(samples) {
        this.sentFrames++;
        this.root?.setAttribute('data-voice-sent-frames', String(this.sentFrames));
        this.dotNet.invokeMethodAsync('ReceiveMicrophoneFrame', encodeFrame(samples)).catch(() => { });
    }

    stopCapture() {
        this.captureGeneration++;
        this.removeCapture();
        this.suspendWhenIdle();
    }

    removeCapture() {
        const capture = this.capture;
        if (!capture) {
            return;
        }

        this.capture = null;
        capture.node.port.onmessage = null;
        capture.node.port.postMessage('stop');
        capture.source.disconnect();
        capture.node.disconnect();
        for (const track of capture.stream.getAudioTracks()) {
            track.onended = null;
        }

        stopTracks(capture.stream);
    }

    async startPlayback() {
        this.pendingStarts++;
        try {
            return await this.tryStartPlayback();
        } finally {
            this.pendingStarts--;
            this.suspendWhenIdle();
        }
    }

    async tryStartPlayback() {
        const generation = this.playbackGeneration;
        try {
            const context = await this.ensureContext();
            if (generation !== this.playbackGeneration) {
                return NOT_STARTED;
            }

            if (!this.playback) {
                const options = this.options;
                this.playback = new AudioWorkletNode(context, 'game-voice-playback', {
                    numberOfInputs: 0,
                    numberOfOutputs: 1,
                    outputChannelCount: [1],
                    processorOptions: {
                        sampleRate: options.sampleRate,
                        frameSamples: options.frameSamples,
                        bufferStartSamples: options.bufferStartSamples,
                        bufferMaximumSamples: options.bufferMaximumSamples,
                        bufferGrowthSamples: options.bufferGrowthSamples,
                        bufferHeadroomSamples: options.bufferHeadroomSamples,
                        bufferShrinkDelaySeconds: options.bufferShrinkDelaySeconds,
                        trimWindowSeconds: options.trimWindowSeconds,
                        rampSeconds: options.rampSeconds,
                    },
                });
                this.playback.port.onmessage = event => this.count(event.data);
                this.playback.connect(context.destination);
                this.count({ underruns: 0, trims: 0 });
            }

            return STARTED;
        } catch {
            return NOT_STARTED;
        }
    }

    stopPlayback() {
        this.playbackGeneration++;
        const playback = this.playback;
        if (playback) {
            this.playback = null;
            playback.port.onmessage = null;
            playback.port.postMessage('stop');
            playback.disconnect();
        }

        this.suspendWhenIdle();
    }

    // The processor reports what it counted since its last report, so the totals add up across playbacks.
    count({ underruns, trims }) {
        this.underruns += underruns;
        this.trims += trims;
        this.root?.setAttribute('data-voice-underruns', String(this.underruns));
        this.root?.setAttribute('data-voice-trimmed', String(this.trims));
    }

    play(frame) {
        if (!this.playback) {
            return;
        }

        const samples = decodeFrame(frame);
        this.playback.port.postMessage(samples, [samples.buffer]);
        this.receivedFrames++;
        this.root?.setAttribute('data-voice-received-frames', String(this.receivedFrames));
    }

    dispose() {
        this.isDisposed = true;
        this.root?.removeEventListener('click', this.onClick, true);
        window.removeEventListener('keydown', this.onKeyDown, true);
        this.stopCapture();
        this.stopPlayback();
        this.context?.close().catch(() => { });
        this.context = null;
    }
}
