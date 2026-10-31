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

// Seconds between two reports of the playback counters to the main thread, sent only after a count changed.
const REPORT_INTERVAL_SECONDS = 0.25;

class PlaybackProcessor extends AudioWorkletProcessor {
    constructor(options) {
        super();
        const settings = options.processorOptions;
        this.step = settings.sampleRate / sampleRate;
        this.startSamples = settings.bufferStartSamples;
        this.maximumSamples = settings.bufferMaximumSamples;
        this.growthSamples = settings.bufferGrowthSamples;
        this.headroomSamples = settings.bufferHeadroomSamples;
        this.shrinkDelay = settings.bufferShrinkDelaySeconds;
        this.trimWindow = settings.trimWindowSeconds;
        this.targetSamples = this.startSamples;
        // Room for bursts above the largest target; only beyond it does arrival drop audio.
        this.capacity = 2 * this.maximumSamples;
        this.ring = new Float32Array(this.capacity);
        this.readIndex = 0;
        this.available = 0;
        this.fraction = 0;
        this.isPlaying = false;
        this.isStopped = false;
        this.lastDry = currentTime;
        this.windowStart = currentTime;
        this.windowMinimum = Infinity;
        // Output samples at the context rate over which a step in the output is blended out.
        this.rampSamples = Math.max(1, Math.round(settings.rampSeconds * sampleRate));
        this.rampRemaining = 0;
        this.rampFrom = 0;
        this.lastOutput = 0;
        // Counted since the last report, which sends and resets them.
        this.underruns = 0;
        this.trims = 0;
        this.lastReport = currentTime;
        this.port.onmessage = event => {
            if (event.data === 'stop') {
                this.isStopped = true;
            } else {
                this.enqueue(event.data);
            }
        };
    }

    enqueue(samples) {
        const excess = this.available + samples.length - this.capacity;
        if (excess > 0) {
            this.drop(excess);
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
            this.startRamp();
        }
    }

    process(inputs, outputs) {
        const output = outputs[0][0];
        for (let index = 0; index < output.length; index++) {
            const value = this.blend(this.isPlaying ? this.next() : 0);
            output[index] = value;
            this.lastOutput = value;
        }

        // Arrivals land only between two calls, so the level after a call is the lowest it reached.
        this.windowMinimum = Math.min(this.windowMinimum, this.available);
        this.trimAtWindowEnd();
        this.shrinkWhenSteady();
        this.report();
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

    // The lowest level over a window is the delay that bursts of arrival or of rendering did not cause, so dropping
    // down to the target there removes standing delay without cutting into a burst that the following gap consumes.
    trimAtWindowEnd() {
        if (currentTime - this.windowStart < this.trimWindow) {
            return;
        }

        if (this.windowMinimum > this.targetSamples + this.headroomSamples) {
            this.drop(this.windowMinimum - this.targetSamples);
        }

        this.windowStart = currentTime;
        this.windowMinimum = Infinity;
    }

    drop(count) {
        this.skip(count);
        this.trims++;
        this.startRamp();
    }

    // Starting, running dry and dropping audio each make the output jump, which clicks; the ramp glides from the last
    // output instead.
    startRamp() {
        this.rampFrom = this.lastOutput;
        this.rampRemaining = this.rampSamples;
    }

    blend(value) {
        if (this.rampRemaining === 0) {
            return value;
        }

        const weight = this.rampRemaining / this.rampSamples;
        this.rampRemaining--;
        return value + (this.rampFrom - value) * weight;
    }

    runDry() {
        this.isPlaying = false;
        this.underruns++;
        this.startRamp();
        this.targetSamples = Math.min(this.maximumSamples, this.targetSamples + this.growthSamples);
        this.lastDry = currentTime;
    }

    shrinkWhenSteady() {
        if (this.isPlaying && this.targetSamples > this.startSamples && currentTime - this.lastDry >= this.shrinkDelay) {
            this.targetSamples = Math.max(this.startSamples, this.targetSamples - this.growthSamples);
            this.lastDry = currentTime;
        }
    }

    report() {
        if ((this.underruns > 0 || this.trims > 0) && currentTime - this.lastReport >= REPORT_INTERVAL_SECONDS) {
            this.port.postMessage({ underruns: this.underruns, trims: this.trims });
            this.underruns = 0;
            this.trims = 0;
            this.lastReport = currentTime;
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
