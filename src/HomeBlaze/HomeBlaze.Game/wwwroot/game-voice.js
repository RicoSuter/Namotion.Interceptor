// The pages of a tab share this module, so each one owns its voice by the id it passes.
const voices = new Map();
const WORKLET_URL = new URL('./game-voice-worklet.js', import.meta.url).href;
const STARTED = { isStarted: true, error: null };
const NOT_STARTED = { isStarted: false, error: 'failed' };

/** Encodes the 16-bit samples of a frame as little-endian bytes; the one place in JavaScript to change for another codec. */
export function encodeFrame(samples) {
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

/** Prepares the voice of the view; returns false where the browser offers no microphone or audio worklet, as on plain HTTP. */
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

/** Resumes a suspended audio context; browsers allow it only from a click. */
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
        this.contextLoading = null;
        this.context = null;
        this.capture = null;
        this.playback = null;
        this.sentFrames = 0;
        this.receivedFrames = 0;
        this.isDisposed = false;
    }

    // Created on the first click of either button, because browsers start audio only from a user action.
    async ensureContext() {
        this.contextLoading ??= this.createContext();
        try {
            this.context = await this.contextLoading;
        } catch (error) {
            this.contextLoading = null;
            throw error;
        }

        this.resume();
        return this.context;
    }

    async createContext() {
        const context = new AudioContext();
        try {
            await context.audioWorklet.addModule(WORKLET_URL);
        } catch (error) {
            context.close().catch(() => { });
            throw error;
        }

        if (this.isDisposed) {
            context.close().catch(() => { });
            throw new Error('The voice was disposed while it started.');
        }

        context.onstatechange = () => this.reportState();
        return context;
    }

    resume() {
        const context = this.context;
        if (context && context.state !== 'running' && context.state !== 'closed') {
            context.resume().then(() => this.reportState(), () => this.reportState());
        }
    }

    reportState() {
        const state = this.context?.state;
        if (!this.isDisposed && state && state !== 'closed') {
            this.dotNet.invokeMethodAsync('OnAudioStateChanged', state !== 'running').catch(() => { });
        }
    }

    async startCapture() {
        let stream = null;
        try {
            const context = await this.ensureContext();
            stream = await navigator.mediaDevices.getUserMedia({
                audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true },
            });
            if (this.isDisposed) {
                stopTracks(stream);
                return NOT_STARTED;
            }

            this.stopCapture();
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
            return STARTED;
        } catch (error) {
            if (stream) {
                stopTracks(stream);
            }

            return { isStarted: false, error: toError(error) };
        }
    }

    send(samples) {
        this.sentFrames++;
        this.options.element?.setAttribute('data-voice-sent-frames', String(this.sentFrames));
        this.dotNet.invokeMethodAsync('ReceiveMicrophoneFrame', encodeFrame(samples)).catch(() => { });
    }

    stopCapture() {
        const capture = this.capture;
        if (!capture) {
            return;
        }

        this.capture = null;
        capture.node.port.onmessage = null;
        capture.node.port.postMessage('stop');
        capture.source.disconnect();
        capture.node.disconnect();
        stopTracks(capture.stream);
    }

    async startPlayback() {
        try {
            const context = await this.ensureContext();
            if (this.isDisposed) {
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
                        bufferShrinkDelaySeconds: options.bufferShrinkDelaySeconds,
                    },
                });
                this.playback.connect(context.destination);
            }

            this.reportState();
            return STARTED;
        } catch {
            return NOT_STARTED;
        }
    }

    stopPlayback() {
        const playback = this.playback;
        if (!playback) {
            return;
        }

        this.playback = null;
        playback.port.postMessage('stop');
        playback.disconnect();
    }

    play(frame) {
        if (!this.playback) {
            return;
        }

        const samples = decodeFrame(frame);
        this.playback.port.postMessage(samples, [samples.buffer]);
        this.receivedFrames++;
        this.options.element?.setAttribute('data-voice-received-frames', String(this.receivedFrames));
    }

    dispose() {
        this.isDisposed = true;
        this.stopCapture();
        this.stopPlayback();
        this.context?.close().catch(() => { });
        this.context = null;
    }
}
