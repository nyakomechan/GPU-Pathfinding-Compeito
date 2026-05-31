export class PathfindEngine {
    constructor(gl, shaders) {
        this.gl = gl;
        this.shaders = shaders;
        this.initialized = false;
        this.svoData = null;
        this.texSize = [0, 0];
        this.nodeCount = 0;

        this.tSVO = null;
        this.tNeighbors1 = null;
        this.tNeighbors2 = null;
        this.tDataA = null;
        this.tDataB = null;
        this.tGoal = null;

        this.fboA = null;
        this.fboB = null;
        this.fboGoal = null;
        this.fboReadTemp = null;

        this.programWavefront = null;
        this.programGoal = null;

        this.quadVAO = null;

        this.currentRead = 'A';
        this.iteration = 0;
        this.goalReached = false;
        this.startIdx = -1;
        this.goalIdx = -1;

        this._uniformCache = new Map();
    }

    init(svoData) {
        this.svoData = svoData;
        this.nodeCount = svoData.leafCount;
        const gl = this.gl;

        const texW = Math.ceil(Math.sqrt(this.nodeCount));
        const texH = Math.ceil(this.nodeCount / texW);
        this.texSize = [texW, texH];

        this._createQuadVAO();
        this._createShaders();
        this._createTextures(svoData);
        this._createFBOs();

        this.initialized = true;
    }

    _getUniform(program, name) {
        const key = program.__id + ':' + name;
        if (this._uniformCache.has(key)) return this._uniformCache.get(key);
        const loc = this.gl.getUniformLocation(program, name);
        this._uniformCache.set(key, loc);
        return loc;
    }

    _createQuadVAO() {
        const gl = this.gl;
        const vao = gl.createVertexArray();
        gl.bindVertexArray(vao);

        const buf = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, buf);
        gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
        gl.enableVertexAttribArray(0);
        gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);

        gl.bindVertexArray(null);
        this.quadVAO = vao;
    }

    _compileShader(type, source) {
        const gl = this.gl;
        const shader = gl.createShader(type);
        gl.shaderSource(shader, source);
        gl.compileShader(shader);
        if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
            const info = gl.getShaderInfoLog(shader);
            gl.deleteShader(shader);
            throw new Error('Shader compile error:\n' + info + '\nSource:\n' + source);
        }
        return shader;
    }

    _createProgram(vsSource, fsSource) {
        const gl = this.gl;
        const vs = this._compileShader(gl.VERTEX_SHADER, vsSource);
        const fs = this._compileShader(gl.FRAGMENT_SHADER, fsSource);
        const prog = gl.createProgram();
        gl.attachShader(prog, vs);
        gl.attachShader(prog, fs);
        gl.bindAttribLocation(prog, 0, 'aPosition');
        gl.linkProgram(prog);
        if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) {
            throw new Error('Program link error: ' + gl.getProgramInfoLog(prog));
        }
        prog.__id = this._progIdCounter++;
        return prog;
    }

    _createShaders() {
        this._progIdCounter = 0;
        const S = this.shaders;
        this.programWavefront = this._createProgram(S.VERT_FULLSCREEN, S.FRAG_WAVEFRONT);
        this.programGoal = this._createProgram(S.VERT_FULLSCREEN, S.FRAG_GOAL_DETECT);
    }

    _createFloatTexture(width, height, data) {
        const gl = this.gl;
        const tex = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, tex);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA32F, width, height, 0, gl.RGBA, gl.FLOAT, data);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        gl.bindTexture(gl.TEXTURE_2D, null);
        return tex;
    }

    _createTextures(svoData) {
        const [tw, th] = this.texSize;
        const n = this.nodeCount;

        const svoArr = new Float32Array(tw * th * 4);
        const n1Arr = new Float32Array(tw * th * 4);
        const n2Arr = new Float32Array(tw * th * 4);

        for (let i = 0; i < n; i++) {
            const ln = svoData.leafNodes[i];
            const off = i * 4;
            svoArr[off + 0] = ln.occupied ? 1.0 : 0.0;
            svoArr[off + 1] = ln.level;
            svoArr[off + 2] = ln.leafX;
            svoArr[off + 3] = ln.leafY;

            for (let d = 0; d < 4; d++) n1Arr[off + d] = svoData.neighbors[i * 6 + d];
            for (let d = 0; d < 2; d++) n2Arr[off + d] = svoData.neighbors[i * 6 + 4 + d];
        }

        this.tSVO = this._createFloatTexture(tw, th, svoArr);
        this.tNeighbors1 = this._createFloatTexture(tw, th, n1Arr);
        this.tNeighbors2 = this._createFloatTexture(tw, th, n2Arr);

        const initData = new Float32Array(tw * th * 4);
        for (let i = 0; i < tw * th; i++) {
            initData[i * 4 + 0] = 999999.0;
            initData[i * 4 + 1] = 0.0;
            initData[i * 4 + 2] = -1.0;
            initData[i * 4 + 3] = 0.0;
        }
        this.tDataA = this._createFloatTexture(tw, th, initData);
        this.tDataB = this._createFloatTexture(tw, th, initData.slice());

        this.tGoal = this._createFloatTexture(1, 1, new Float32Array([0, 0, 0, 1]));
    }

    _createFBO(texture) {
        const gl = this.gl;
        const fbo = gl.createFramebuffer();
        gl.bindFramebuffer(gl.FRAMEBUFFER, fbo);
        gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, texture, 0);
        const status = gl.checkFramebufferStatus(gl.FRAMEBUFFER);
        if (status !== gl.FRAMEBUFFER_COMPLETE) {
            console.error('FBO incomplete:', status, 'for texture', texture);
        }
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);
        return fbo;
    }

    _createFBOs() {
        this.fboA = this._createFBO(this.tDataA);
        this.fboB = this._createFBO(this.tDataB);
        this.fboGoal = this._createFBO(this.tGoal);
    }

    setStart(leafIdx) { this.startIdx = leafIdx; }
    setGoal(leafIdx) { this.goalIdx = leafIdx; }

    reset() {
        const gl = this.gl;
        const [tw, th] = this.texSize;
        this.iteration = 0;
        this.goalReached = false;
        this.currentRead = 'A';

        const dataArr = new Float32Array(tw * th * 4);
        for (let i = 0; i < tw * th; i++) {
            dataArr[i * 4 + 0] = 999999.0;
            dataArr[i * 4 + 1] = 0.0;
            dataArr[i * 4 + 2] = -1.0;
            dataArr[i * 4 + 3] = 0.0;
        }

        if (this.startIdx >= 0 && this.startIdx < this.nodeCount) {
            const off = this.startIdx * 4;
            dataArr[off + 0] = 0.0;
            dataArr[off + 1] = 1.0;
            dataArr[off + 2] = -1.0;
        }

        gl.bindTexture(gl.TEXTURE_2D, this.tDataA);
        gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, tw, th, gl.RGBA, gl.FLOAT, dataArr);
        gl.bindTexture(gl.TEXTURE_2D, this.tDataB);
        gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, tw, th, gl.RGBA, gl.FLOAT, dataArr);
        gl.bindTexture(gl.TEXTURE_2D, null);
    }

    iterate(count) {
        if (!this.initialized || this.startIdx < 0 || this.goalIdx < 0) return false;
        if (this.goalReached) return true;

        const gl = this.gl;
        const [tw, th] = this.texSize;

        gl.bindVertexArray(this.quadVAO);
        gl.useProgram(this.programWavefront);

        gl.uniform2f(this._getUniform(this.programWavefront, 'uTexSize'), tw, th);
        gl.uniform1i(this._getUniform(this.programWavefront, 'uNodeCount'), this.nodeCount);

        gl.activeTexture(gl.TEXTURE0);
        gl.bindTexture(gl.TEXTURE_2D, this.tSVO);
        gl.uniform1i(this._getUniform(this.programWavefront, 'tSVO'), 0);

        gl.activeTexture(gl.TEXTURE1);
        gl.bindTexture(gl.TEXTURE_2D, this.tNeighbors1);
        gl.uniform1i(this._getUniform(this.programWavefront, 'tNeighbors1'), 1);

        gl.activeTexture(gl.TEXTURE2);
        gl.bindTexture(gl.TEXTURE_2D, this.tNeighbors2);
        gl.uniform1i(this._getUniform(this.programWavefront, 'tNeighbors2'), 2);

        for (let i = 0; i < count; i++) {
            const readTex = this.currentRead === 'A' ? this.tDataA : this.tDataB;
            const writeFBO = this.currentRead === 'A' ? this.fboB : this.fboA;

            gl.activeTexture(gl.TEXTURE3);
            gl.bindTexture(gl.TEXTURE_2D, readTex);
            gl.uniform1i(this._getUniform(this.programWavefront, 'tData'), 3);

            gl.bindFramebuffer(gl.FRAMEBUFFER, writeFBO);
            gl.viewport(0, 0, tw, th);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);

            this.currentRead = this.currentRead === 'A' ? 'B' : 'A';
            this.iteration++;
        }

        const readTex = this.currentRead === 'A' ? this.tDataA : this.tDataB;

        gl.useProgram(this.programGoal);
        gl.activeTexture(gl.TEXTURE0);
        gl.bindTexture(gl.TEXTURE_2D, readTex);
        gl.uniform1i(this._getUniform(this.programGoal, 'tData'), 0);
        gl.uniform1f(this._getUniform(this.programGoal, 'uGoalIdx'), this.goalIdx);
        gl.uniform2f(this._getUniform(this.programGoal, 'uTexSize'), tw, th);

        gl.bindFramebuffer(gl.FRAMEBUFFER, this.fboGoal);
        gl.viewport(0, 0, 1, 1);
        gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);

        const pixels = new Float32Array(4);
        gl.readPixels(0, 0, 1, 1, gl.RGBA, gl.FLOAT, pixels);
        this.goalReached = pixels[0] > 0.4;

        gl.bindVertexArray(null);
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);

        return this.goalReached;
    }

    readResult() {
        const gl = this.gl;
        const [tw, th] = this.texSize;
        const readTex = this.currentRead === 'A' ? this.tDataA : this.tDataB;

        gl.bindFramebuffer(gl.FRAMEBUFFER, this.currentRead === 'A' ? this.fboA : this.fboB);
        gl.viewport(0, 0, tw, th);

        const pixels = new Float32Array(tw * th * 4);
        gl.readPixels(0, 0, tw, th, gl.RGBA, gl.FLOAT, pixels);
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);

        const result = new Array(this.nodeCount);
        for (let i = 0; i < this.nodeCount; i++) {
            result[i] = {
                cost: pixels[i * 4 + 0],
                state: pixels[i * 4 + 1],
                parent: pixels[i * 4 + 2],
            };
        }
        return result;
    }

    reconstructPath(result) {
        if (!this.goalReached) return null;
        if (!result) result = this.readResult();

        const path = [];
        let current = this.goalIdx;
        const visited = new Set();

        while (current >= 0 && current < this.nodeCount) {
            if (visited.has(current)) break;
            visited.add(current);
            path.push(current);
            if (current === this.startIdx) break;
            const parentIdx = Math.round(result[current].parent);
            if (parentIdx < 0 || parentIdx === current) break;
            current = parentIdx;
        }

        path.reverse();
        return path;
    }
}
