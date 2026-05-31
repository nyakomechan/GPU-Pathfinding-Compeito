import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

const WALL_COLOR = 0x4a4a6a;
const FRONTIER_COLOR = 0x40916c;
const VISITED_COLOR = 0x2d6a4f;
const PATH_COLOR = 0xffd166;
const START_COLOR = 0x06d6a0;
const GOAL_COLOR = 0xef476f;
const SLICE_COLOR = 0xffd166;

export class Visualizer {
    constructor(canvas, svoData) {
        this.svoData = svoData;
        this.gs = svoData.gridSize;
        this.zSlice = Math.floor(this.gs / 2);
        this.showSlice = true;
        this.placeMode = false;

        this._initThree(canvas);
        this._buildStaticGeometry();
        this._buildDynamicGeometry();
    }

    _initThree(canvas) {
        const container = canvas.parentElement;
        const w = container.clientWidth;
        const h = container.clientHeight;

        this.renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: false });
        this.renderer.setClearColor(0x0f0f23, 1);
        this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
        this.renderer.setSize(w, h);

        this.scene = new THREE.Scene();

        this.camera = new THREE.PerspectiveCamera(50, w / h, 0.1, 500);
        const gs = this.gs;
        this.camera.position.set(gs * 1.8, gs * 1.6, gs * 1.8);
        this.camera.lookAt(gs / 2, gs / 2, gs / 2);

        this.controls = new OrbitControls(this.camera, canvas);
        this.controls.target.set(gs / 2, gs / 2, gs / 2);
        this.controls.enableDamping = true;
        this.controls.dampingFactor = 0.08;
        this.controls.update();

        const ambient = new THREE.AmbientLight(0xffffff, 0.6);
        this.scene.add(ambient);
        const dir = new THREE.DirectionalLight(0xffffff, 0.8);
        dir.position.set(gs * 2, gs * 3, gs * 2);
        this.scene.add(dir);

        this.raycaster = new THREE.Raycaster();
        this.mouse = new THREE.Vector2();
        this.canvas = canvas;

        this._onResize = () => {
            const cw = container.clientWidth;
            const ch = container.clientHeight;
            this.camera.aspect = cw / ch;
            this.camera.updateProjectionMatrix();
            this.renderer.setSize(cw, ch);
        };
        window.addEventListener('resize', this._onResize);
    }

    _buildStaticGeometry() {
        const gs = this.gs;
        const sd = this.svoData;

        const wallPositions = [];
        for (let z = 0; z < gs; z++) {
            for (let y = 0; y < gs; y++) {
                for (let x = 0; x < gs; x++) {
                    const vi = x + y * gs + z * gs * gs;
                    if (sd.voxelToLeaf[vi] === -1) {
                        wallPositions.push(x, y, z);
                    }
                }
            }
        }

        const wallCount = wallPositions.length / 3;
        const boxGeo = new THREE.BoxGeometry(1, 1, 1);
        const wallMat = new THREE.MeshLambertMaterial({ color: WALL_COLOR, transparent: true, opacity: 0.5 });
        this.wallMesh = new THREE.InstancedMesh(boxGeo, wallMat, wallCount);
        const dummy = new THREE.Object3D();

        for (let i = 0; i < wallCount; i++) {
            dummy.position.set(wallPositions[i * 3] + 0.5, wallPositions[i * 3 + 1] + 0.5, wallPositions[i * 3 + 2] + 0.5);
            dummy.updateMatrix();
            this.wallMesh.setMatrixAt(i, dummy.matrix);
        }
        this.wallMesh.instanceMatrix.needsUpdate = true;
        this.scene.add(this.wallMesh);

        const borderGeo = new THREE.BoxGeometry(gs, gs, gs);
        const borderMat = new THREE.MeshBasicMaterial({ color: 0x333355, wireframe: true });
        const borderMesh = new THREE.Mesh(borderGeo, borderMat);
        borderMesh.position.set(gs / 2, gs / 2, gs / 2);
        this.scene.add(borderMesh);

        const sliceGeo = new THREE.PlaneGeometry(gs, gs);
        const sliceMat = new THREE.MeshBasicMaterial({ color: SLICE_COLOR, transparent: true, opacity: 0.08, side: THREE.DoubleSide });
        this.slicePlane = new THREE.Mesh(sliceGeo, sliceMat);
        this.slicePlane.rotation.x = -Math.PI / 2;
        this.slicePlane.position.set(gs / 2, this.zSlice + 0.5, gs / 2);
        this.scene.add(this.slicePlane);

        const sliceEdgeGeo = new THREE.EdgesGeometry(new THREE.PlaneGeometry(gs, gs));
        const sliceEdgeMat = new THREE.LineBasicMaterial({ color: SLICE_COLOR, transparent: true, opacity: 0.4 });
        this.sliceEdge = new THREE.LineSegments(sliceEdgeGeo, sliceEdgeMat);
        this.sliceEdge.rotation.x = -Math.PI / 2;
        this.sliceEdge.position.set(gs / 2, this.zSlice + 0.5, gs / 2);
        this.scene.add(this.sliceEdge);
    }

    _buildDynamicGeometry() {
        const gs = this.gs;
        const sd = this.svoData;
        const maxNodes = sd.leafCount;

        const nodeGeo = new THREE.BoxGeometry(0.9, 0.9, 0.9);
        const nodeMat = new THREE.MeshLambertMaterial({ transparent: true, opacity: 0.7 });
        this.frontierMesh = new THREE.InstancedMesh(nodeGeo, nodeMat.clone(), maxNodes);
        this.frontierMesh.material.color.setHex(FRONTIER_COLOR);
        this.frontierMesh.count = 0;
        this.scene.add(this.frontierMesh);

        this.visitedMesh = new THREE.InstancedMesh(nodeGeo, nodeMat.clone(), maxNodes);
        this.visitedMesh.material.color.setHex(VISITED_COLOR);
        this.visitedMesh.material.opacity = 0.5;
        this.visitedMesh.count = 0;
        this.scene.add(this.visitedMesh);

        this.pathLine = null;

        const markerGeo = new THREE.SphereGeometry(0.5, 16, 16);
        this.startMarker = new THREE.Mesh(markerGeo, new THREE.MeshLambertMaterial({ color: START_COLOR, emissive: START_COLOR, emissiveIntensity: 0.3 }));
        this.startMarker.visible = false;
        this.scene.add(this.startMarker);

        this.goalMarker = new THREE.Mesh(markerGeo, new THREE.MeshLambertMaterial({ color: GOAL_COLOR, emissive: GOAL_COLOR, emissiveIntensity: 0.3 }));
        this.goalMarker.visible = false;
        this.scene.add(this.goalMarker);
    }

    draw(pathResult, path, startIdx, goalIdx) {
        const gs = this.gs;
        const sd = this.svoData;
        const dummy = new THREE.Object3D();

        let frontierCount = 0;
        let visitedCount = 0;

        if (pathResult) {
            for (let i = 0; i < sd.leafCount; i++) {
                const ln = sd.leafNodes[i];
                const st = pathResult[i].state;
                if (st > 1.5) {
                    dummy.position.set(ln.leafX + 0.5, ln.leafY + 0.5, ln.leafZ + 0.5);
                    dummy.updateMatrix();
                    this.visitedMesh.setMatrixAt(visitedCount, dummy.matrix);
                    visitedCount++;
                } else if (st > 0.5) {
                    dummy.position.set(ln.leafX + 0.5, ln.leafY + 0.5, ln.leafZ + 0.5);
                    dummy.updateMatrix();
                    this.frontierMesh.setMatrixAt(frontierCount, dummy.matrix);
                    frontierCount++;
                }
            }
        }

        this.frontierMesh.count = frontierCount;
        this.frontierMesh.instanceMatrix.needsUpdate = true;
        this.visitedMesh.count = visitedCount;
        this.visitedMesh.instanceMatrix.needsUpdate = true;

        if (this.pathLine) {
            this.scene.remove(this.pathLine);
            this.pathLine.geometry.dispose();
            this.pathLine.material.dispose();
            this.pathLine = null;
        }

        if (path && path.length > 1) {
            const pts = [];
            for (const idx of path) {
                const ln = sd.leafNodes[idx];
                pts.push(new THREE.Vector3(ln.leafX + 0.5, ln.leafY + 0.5, ln.leafZ + 0.5));
            }
            const geo = new THREE.BufferGeometry().setFromPoints(pts);
            const mat = new THREE.LineBasicMaterial({ color: PATH_COLOR, linewidth: 2 });
            this.pathLine = new THREE.Line(geo, mat);
            this.scene.add(this.pathLine);
        }

        if (startIdx >= 0 && startIdx < sd.leafCount) {
            const ln = sd.leafNodes[startIdx];
            this.startMarker.position.set(ln.leafX + 0.5, ln.leafY + 0.5, ln.leafZ + 0.5);
            this.startMarker.visible = true;
        } else {
            this.startMarker.visible = false;
        }

        if (goalIdx >= 0 && goalIdx < sd.leafCount) {
            const ln = sd.leafNodes[goalIdx];
            this.goalMarker.position.set(ln.leafX + 0.5, ln.leafY + 0.5, ln.leafZ + 0.5);
            this.goalMarker.visible = true;
        } else {
            this.goalMarker.visible = false;
        }

        this._updateSlice();
    }

    _updateSlice() {
        this.slicePlane.position.y = this.zSlice + 0.5;
        this.sliceEdge.position.y = this.zSlice + 0.5;
        const show = this.showSlice;
        this.slicePlane.visible = show;
        this.sliceEdge.visible = show;
    }

    setZSlice(z) {
        this.zSlice = z;
        this._updateSlice();
    }

    setShowSlice(show) {
        this.showSlice = show;
        this._updateSlice();
    }

    setPlaceMode(enabled) {
        this.placeMode = enabled;
        this.controls.enabled = !enabled;
        this.canvas.style.cursor = enabled ? 'crosshair' : 'grab';
    }

    pickVoxel(clientX, clientY) {
        const rect = this.canvas.getBoundingClientRect();
        this.mouse.x = ((clientX - rect.left) / rect.width) * 2 - 1;
        this.mouse.y = -((clientY - rect.top) / rect.height) * 2 + 1;

        this.raycaster.setFromCamera(this.mouse, this.camera);

        const plane = new THREE.Plane(new THREE.Vector3(0, 1, 0), -(this.zSlice + 0.5));
        const intersection = new THREE.Vector3();
        this.raycaster.ray.intersectPlane(plane, intersection);

        if (!intersection) return -1;

        const vx = Math.floor(intersection.x);
        const vy = Math.floor(intersection.y);
        const vz = Math.floor(intersection.z);
        const gs = this.gs;

        if (vx < 0 || vx >= gs || vy < 0 || vy >= gs || vz < 0 || vz >= gs) return -1;
        if (vy !== this.zSlice) return -1;

        const vi = vx + vy * gs + vz * gs * gs;
        return this.svoData.voxelToLeaf[vi];
    }

    render() {
        this.controls.update();
        this.renderer.render(this.scene, this.camera);
    }

    dispose() {
        window.removeEventListener('resize', this._onResize);
        this.renderer.dispose();
    }
}
