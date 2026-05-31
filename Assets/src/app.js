import { SVOBuilder } from './svo-builder.js';
import { PathfindEngine } from './pathfind-engine.js';
import { Visualizer } from './visualizer.js';
import { VERT_FULLSCREEN, FRAG_WAVEFRONT, FRAG_GOAL_DETECT } from './shaders.js';

const GRID_SIZE = 16;
const ITERS_PER_FRAME = 8;

let engine = null;
let viz = null;
let svoData = null;

let startLeafIdx = -1;
let goalLeafIdx = -1;
let clickMode = 'start';
let placeMode = false;
let running = false;
let pathFound = false;
let currentPath = null;
let lastResult = null;

function mulberry32(a) {
    return function () {
        let t = (a += 0x6d2b79f5);
        t = Math.imul(t ^ (t >>> 15), t | 1);
        t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
        return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
}

function buildGrid() {
    const gs = GRID_SIZE;
    const g = new Uint8Array(gs * gs * gs).fill(0);
    const rng = mulberry32(42);

    const idx = (x, y, z) => x + y * gs + z * gs * gs;

    for (let z = 0; z < gs; z++)
        for (let y = 0; y < gs; y++)
            for (let x = 0; x < gs; x++)
                if (x === 0 || x === gs - 1 || y === 0 || y === gs - 1 || z === 0 || z === gs - 1)
                    g[idx(x, y, z)] = 1;

    const blockSize = 4;
    const numBlocks = 8;
    const placed = [];

    for (let b = 0; b < numBlocks; b++) {
        let attempts = 0;
        while (attempts < 50) {
            const bx = 1 + Math.floor(rng() * (gs - 2 - blockSize));
            const by = 1 + Math.floor(rng() * (gs - 2 - blockSize));
            const bz = 1 + Math.floor(rng() * (gs - 2 - blockSize));

            let overlaps = false;
            for (const p of placed) {
                if (bx < p.x + blockSize && bx + blockSize > p.x &&
                    by < p.y + blockSize && by + blockSize > p.y &&
                    bz < p.z + blockSize && bz + blockSize > p.z) {
                    overlaps = true;
                    break;
                }
            }

            if (!overlaps) {
                placed.push({ x: bx, y: by, z: bz });
                for (let dz = 0; dz < blockSize; dz++)
                    for (let dy = 0; dy < blockSize; dy++)
                        for (let dx = 0; dx < blockSize; dx++)
                            g[idx(bx + dx, by + dy, bz + dz)] = 1;
                break;
            }
            attempts++;
        }
    }

    return g;
}

function createGLContext() {
    const canvas = document.getElementById('glCanvas');
    const gl = canvas.getContext('webgl2');
    if (!gl) throw new Error('WebGL 2 not supported');
    const ext = gl.getExtension('EXT_color_buffer_float');
    if (!ext) throw new Error('EXT_color_buffer_float not supported');
    gl.getExtension('OES_texture_float_linear');
    return gl;
}

function findEmptyLeaf(x0, y0, z0, x1, y1, z1, dx, dy, dz) {
    for (let z = z0; z !== z1 + dz; z += dz)
        for (let y = y0; y !== y1 + dy; y += dy)
            for (let x = x0; x !== x1 + dx; x += dx) {
                const vi = x + y * GRID_SIZE + z * GRID_SIZE * GRID_SIZE;
                if (svoData.voxelToLeaf[vi] >= 0) return svoData.voxelToLeaf[vi];
            }
    return -1;
}

function init() {
    const gl = createGLContext();
    const builder = new SVOBuilder(GRID_SIZE);
    const g = buildGrid();

    for (let z = 0; z < GRID_SIZE; z++)
        for (let y = 0; y < GRID_SIZE; y++)
            for (let x = 0; x < GRID_SIZE; x++)
                builder.setVoxel(x, y, z, g[x + y * GRID_SIZE + z * GRID_SIZE * GRID_SIZE] === 1);

    svoData = builder.build();
    console.log('SVO built:', svoData.leafCount, 'leaf nodes,', svoData.nodes.length, 'total nodes');

    const shaders = { VERT_FULLSCREEN, FRAG_WAVEFRONT, FRAG_GOAL_DETECT };
    engine = new PathfindEngine(gl, shaders);
    engine.init(svoData);

    const vizCanvas = document.getElementById('vizCanvas');
    viz = new Visualizer(vizCanvas, svoData);

    startLeafIdx = findEmptyLeaf(1, 1, 1, GRID_SIZE - 2, GRID_SIZE - 2, 1, 1, 1, 1);
    goalLeafIdx = findEmptyLeaf(GRID_SIZE - 2, GRID_SIZE - 2, GRID_SIZE - 2, 1, 1, 1, -1, -1, -1);

    engine.setStart(startLeafIdx);
    engine.setGoal(goalLeafIdx);
    engine.reset();

    updateInfo();
    viz.draw(null, null, startLeafIdx, goalLeafIdx);
}

function startPathfinding() {
    if (startLeafIdx < 0 || goalLeafIdx < 0) return;
    running = true;
    pathFound = false;
    currentPath = null;
    lastResult = null;
    engine.reset();
    updateInfo();
}

function stepOnce() {
    if (startLeafIdx < 0 || goalLeafIdx < 0) return;
    if (pathFound) return;

    if (engine.iteration === 0) engine.reset();

    const reached = engine.iterate(1);
    lastResult = engine.readResult();

    if (reached) {
        pathFound = true;
        running = false;
        currentPath = engine.reconstructPath(lastResult);
    }

    viz.draw(lastResult, currentPath, startLeafIdx, goalLeafIdx);
    updateInfo();
}

function animationLoop() {
    if (running && !pathFound) {
        const reached = engine.iterate(ITERS_PER_FRAME);
        lastResult = engine.readResult();

        if (reached) {
            pathFound = true;
            running = false;
            currentPath = engine.reconstructPath(lastResult);
            document.getElementById('btnRun').textContent = 'Run';
        }

        viz.draw(lastResult, currentPath, startLeafIdx, goalLeafIdx);
        updateInfo();
    }

    viz.render();
    requestAnimationFrame(animationLoop);
}

function updateInfo() {
    const el = document.getElementById('info');
    const sn = startLeafIdx >= 0 ? svoData.leafNodes[startLeafIdx] : null;
    const gn = goalLeafIdx >= 0 ? svoData.leafNodes[goalLeafIdx] : null;

    let fc = 0, vc = 0;
    if (lastResult) {
        for (const r of lastResult) {
            if (r.state > 1.5) vc++;
            else if (r.state > 0.5) fc++;
        }
    }

    const status = pathFound
        ? `PATH FOUND (${currentPath ? currentPath.length : 0} steps)`
        : running ? 'Running...' : 'Idle';

    el.innerHTML = `
        <div><b>SVO Info</b></div>
        <div>Grid: ${GRID_SIZE}&times;${GRID_SIZE}&times;${GRID_SIZE}</div>
        <div>Leaf nodes: ${svoData.leafCount}</div>
        <div>Total SVO nodes: ${svoData.nodes.length}</div>
        <div>Z-Slice: ${viz.zSlice}</div>
        <hr>
        <div><b>Pathfinding</b></div>
        <div>Start: ${sn ? `(${sn.leafX},${sn.leafY},${sn.leafZ})` : 'not set'}</div>
        <div>Goal: ${gn ? `(${gn.leafX},${gn.leafY},${gn.leafZ})` : 'not set'}</div>
        <div>Iteration: ${engine.iteration}</div>
        <div>Frontier: ${fc}</div>
        <div>Visited: ${vc}</div>
        <div>Status: ${status}</div>
        <hr>
        <div><b>Place Mode</b>: ${clickMode === 'start' ? 'Set Start' : 'Set Goal'}</div>
        <div><small>Right-click: toggle start/goal</small></div>
    `;
}

function updatePlaceButtons() {
    const btnOrbit = document.getElementById('btnOrbit');
    const btnPlace = document.getElementById('btnPlace');
    const badge = document.getElementById('modeBadge');

    if (placeMode) {
        btnOrbit.classList.remove('active');
        btnPlace.classList.add('active');
        btnPlace.textContent = clickMode === 'start' ? 'Placing Start...' : 'Placing Goal...';
        badge.textContent = `Place ${clickMode === 'start' ? 'Start' : 'Goal'}`;
        badge.style.color = clickMode === 'start' ? '#06d6a0' : '#ef476f';
    } else {
        btnOrbit.classList.add('active');
        btnPlace.classList.remove('active');
        btnPlace.textContent = 'Place Start';
        badge.textContent = 'Orbit Mode';
        badge.style.color = '#ffd166';
    }

    viz.setPlaceMode(placeMode);
}

window.addEventListener('DOMContentLoaded', () => {
    try {
        init();
    } catch (e) {
        document.getElementById('info').innerHTML = `<div style="color:red">Error: ${e.message}</div>`;
        console.error(e);
        return;
    }

    const vizCanvas = document.getElementById('vizCanvas');

    vizCanvas.addEventListener('click', (e) => {
        if (!placeMode) return;
        const leafIdx = viz.pickVoxel(e.clientX, e.clientY);
        if (leafIdx < 0) return;

        if (clickMode === 'start') {
            startLeafIdx = leafIdx;
            engine.setStart(leafIdx);
        } else {
            goalLeafIdx = leafIdx;
            engine.setGoal(leafIdx);
        }

        pathFound = false;
        running = false;
        currentPath = null;
        lastResult = null;
        engine.reset();
        updateInfo();
        viz.draw(null, null, startLeafIdx, goalLeafIdx);
    });

    vizCanvas.addEventListener('contextmenu', (e) => {
        e.preventDefault();
        if (placeMode) {
            clickMode = clickMode === 'start' ? 'goal' : 'start';
            updatePlaceButtons();
        }
    });

    document.getElementById('btnRun').addEventListener('click', () => {
        if (running) {
            running = false;
            document.getElementById('btnRun').textContent = 'Run';
        } else {
            startPathfinding();
            document.getElementById('btnRun').textContent = 'Pause';
        }
    });

    document.getElementById('btnStep').addEventListener('click', () => {
        running = false;
        document.getElementById('btnRun').textContent = 'Run';
        stepOnce();
    });

    document.getElementById('btnReset').addEventListener('click', () => {
        running = false;
        pathFound = false;
        currentPath = null;
        lastResult = null;
        engine.reset();
        document.getElementById('btnRun').textContent = 'Run';
        updateInfo();
        viz.draw(null, null, startLeafIdx, goalLeafIdx);
    });

    document.getElementById('btnOrbit').addEventListener('click', () => {
        placeMode = false;
        updatePlaceButtons();
    });

    document.getElementById('btnPlace').addEventListener('click', () => {
        placeMode = !placeMode;
        if (placeMode) clickMode = 'start';
        updatePlaceButtons();
    });

    document.getElementById('zSlider').addEventListener('input', (e) => {
        viz.setZSlice(parseInt(e.target.value));
        document.getElementById('zValue').textContent = viz.zSlice;
    });

    document.getElementById('chkSlice').addEventListener('change', (e) => {
        viz.setShowSlice(e.target.checked);
    });

    document.getElementById('zSlider').max = GRID_SIZE - 1;
    document.getElementById('zSlider').value = viz.zSlice;
    document.getElementById('zValue').textContent = viz.zSlice;

    updatePlaceButtons();

    requestAnimationFrame(animationLoop);
});
