// Runs on the audio rendering thread. Both processors work with frames at the voice sample rate and resample from and
// to the rate of the audio context, which the global sampleRate gives. A 'stop' message ends a processor, because one
// whose process returns true keeps its node alive after the node was disconnected.

class CaptureProcessor extends AudioWorkletProcessor {
    constructor(options) {
        super();
        const { sampleRate: voiceRate, frameSamples } = options.processorOptions;
        this.step = sampleRate / voiceRate;
        // Two one-pole low-pass stages below the voice rate's Nyquist frequency keep higher content from folding back.
        this.alpha = 1 - Math.exp(-2 * Math.PI * 0.45 * voiceRate / sampleRate);
        this.firstStage = 0;
        this.secondStage = 0;
        this.previous = 0;
        // Where the next output sample lies, in input samples from the start of the current block.
        this.position = 0;
        this.frameSamples = frameSamples;
        this.frame = new Int16Array(frameSamples);
        this.length = 0;
        this.isStopped = false;
        this.port.onmessage = () => { this.isStopped = true; };
    }

    process(inputs) {
        const input = inputs[0]?.[0];
        if (input && !this.isStopped) {
            this.consume(input);
        }

        return !this.isStopped;
    }

    consume(input) {
        for (let index = 0; index < input.length; index++) {
            this.firstStage += this.alpha * (input[index] - this.firstStage);
            this.secondStage += this.alpha * (this.firstStage - this.secondStage);
            // Interpolates between the previous and the current filtered sample, which lie at index - 1 and index.
            while (this.position <= index) {
                this.push(this.previous + (this.secondStage - this.previous) * (this.position - index + 1));
                this.position += this.step;
            }

            this.previous = this.secondStage;
        }

        this.position -= input.length;
    }

    push(value) {
        this.frame[this.length++] = Math.max(-32768, Math.min(32767, Math.round(value * 32768)));
        if (this.length === this.frameSamples) {
            // The transfer detaches the buffer, which leaves the frame with a length of 0, so the next frame needs a new one.
            this.port.postMessage(this.frame, [this.frame.buffer]);
            this.frame = new Int16Array(this.frameSamples);
            this.length = 0;
        }
    }
}

class PlaybackProcessor extends AudioWorkletProcessor {
    constructor(options) {
        super();
        const settings = options.processorOptions;
        this.step = settings.sampleRate / sampleRate;
        this.startSamples = settings.bufferStartSamples;
        this.maximumSamples = settings.bufferMaximumSamples;
        this.growthSamples = settings.bufferGrowthSamples;
        this.shrinkDelay = settings.bufferShrinkDelaySeconds;
        this.targetSamples = this.startSamples;
        this.capacity = this.maximumSamples + settings.frameSamples;
        this.ring = new Float32Array(this.capacity);
        this.readIndex = 0;
        this.available = 0;
        this.fraction = 0;
        this.isPlaying = false;
        this.isStopped = false;
        this.lastDry = currentTime;
        this.port.onmessage = event => {
            if (event.data === 'stop') {
                this.isStopped = true;
            } else {
                this.enqueue(event.data);
            }
        };
    }

    // Keeps the delay near the target: drops the oldest audio beyond one growth step above it, and beyond the maximum.
    enqueue(samples) {
        const limit = Math.min(this.maximumSamples, this.targetSamples + this.growthSamples);
        const excess = this.available + samples.length - limit;
        if (excess > 0) {
            this.skip(excess);
        }

        // A frame larger than the ring keeps only its newest samples.
        const count = Math.min(samples.length, this.capacity - this.available);
        let writeIndex = (this.readIndex + this.available) % this.capacity;
        for (let index = samples.length - count; index < samples.length; index++) {
            this.ring[writeIndex] = samples[index];
            writeIndex = (writeIndex + 1) % this.capacity;
        }

        this.available += count;
        if (!this.isPlaying && this.available >= this.targetSamples) {
            this.isPlaying = true;
        }
    }

    process(inputs, outputs) {
        const output = outputs[0][0];
        for (let index = 0; index < output.length; index++) {
            output[index] = this.isPlaying ? this.next() : 0;
        }

        this.shrinkWhenSteady();
        return !this.isStopped;
    }

    next() {
        // Interpolating needs the sample after the current one.
        if (this.available < 2) {
            this.runDry();
            return 0;
        }

        const current = this.ring[this.readIndex];
        const following = this.ring[(this.readIndex + 1) % this.capacity];
        const value = current + (following - current) * this.fraction;
        this.fraction += this.step;
        const consumed = Math.floor(this.fraction);
        this.fraction -= consumed;
        this.skip(consumed);
        return value;
    }

    runDry() {
        this.isPlaying = false;
        this.targetSamples = Math.min(this.maximumSamples, this.targetSamples + this.growthSamples);
        this.lastDry = currentTime;
    }

    shrinkWhenSteady() {
        if (this.isPlaying && this.targetSamples > this.startSamples && currentTime - this.lastDry >= this.shrinkDelay) {
            this.targetSamples = Math.max(this.startSamples, this.targetSamples - this.growthSamples);
            this.lastDry = currentTime;
        }
    }

    skip(count) {
        const skipped = Math.min(count, this.available);
        this.readIndex = (this.readIndex + skipped) % this.capacity;
        this.available -= skipped;
    }
}

registerProcessor('game-voice-capture', CaptureProcessor);
registerProcessor('game-voice-playback', PlaybackProcessor);
