(() => {
    // Small shared WebGL setup; canvases remain 2D so unavailable/lost GPU contexts can fall back.
    function create(vertex, fragment, attributes) {
        const canvas = document.createElement('canvas');
        const gl = canvas.getContext('webgl', { antialias: false, premultipliedAlpha: true, preserveDrawingBuffer: true, failIfMajorPerformanceCaveat: true });
        if (!gl) return null;
        const program = gl.createProgram(), buffer = gl.createBuffer();
        try {
            for (const [type, source] of [[gl.VERTEX_SHADER, vertex], [gl.FRAGMENT_SHADER, fragment]]) {
                const shader = gl.createShader(type); gl.shaderSource(shader, source); gl.compileShader(shader);
                if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(shader));
                gl.attachShader(program, shader); gl.deleteShader(shader);
            }
            gl.linkProgram(program);
            if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program));
            gl.useProgram(program); gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
            const stride = attributes.reduce((sum, [, size]) => sum + size, 0) * 4;
            let offset = 0;
            for (const [name, size] of attributes) {
                const location = gl.getAttribLocation(program, name);
                gl.enableVertexAttribArray(location); gl.vertexAttribPointer(location, size, gl.FLOAT, false, stride, offset); offset += size * 4;
            }
            let uploaded = false, capacity = 0, lost = false;
            canvas.addEventListener('webglcontextlost', event => { event.preventDefault(); lost = true; });
            return { canvas, gl, get lost() { return lost || gl.isContextLost(); }, uniform: name => gl.getUniformLocation(program, name),
                draw(vertices, count, update = false) {
                    if (lost || gl.isContextLost()) return false;
                    if (!uploaded || vertices.byteLength > capacity) { gl.bufferData(gl.ARRAY_BUFFER, vertices, update ? gl.DYNAMIC_DRAW : gl.STATIC_DRAW); uploaded = true; capacity = vertices.byteLength; }
                    else if (update) gl.bufferSubData(gl.ARRAY_BUFFER, 0, vertices);
                    gl.viewport(0, 0, canvas.width, canvas.height); gl.drawArrays(gl.TRIANGLES, 0, count); return true;
                },
                dispose() { gl.deleteBuffer(buffer); gl.deleteProgram(program); gl.getExtension('WEBGL_lose_context')?.loseContext(); }
            };
        } catch {
            gl.deleteBuffer(buffer); gl.deleteProgram(program); gl.getExtension('WEBGL_lose_context')?.loseContext(); return null;
        }
    }
    function stars() {
        const gpu = create(`attribute vec2 position; attribute vec3 local; attribute vec4 color;
            uniform vec2 viewport; varying vec3 shape; varying vec4 tint;
            void main(){vec2 p=position/viewport*2.0-1.0;gl_Position=vec4(p.x,-p.y,0,1);shape=local;tint=color;}`,
            `precision mediump float; varying vec3 shape; varying vec4 tint; uniform float feather;
            void main(){float d=shape.z>0.0?shape.z-length(shape.xy):-shape.z-abs(shape.y);
                float a=tint.a*clamp(d/(feather*2.0)+0.5,0.0,1.0);gl_FragColor=vec4(tint.rgb*a,a);}`,
            [['position', 2], ['local', 3], ['color', 4]]);
        if (!gpu) return null;
        const vertices = new Float32Array(650 * 12 * 9), viewport = gpu.uniform('viewport'), feather = gpu.uniform('feather');
        const corners = [-1,-1,1,-1,-1,1,-1,1,1,-1,1,1];
        let at = 0, soft = .5;
        function quad(x, y, ux, uy, vx, vy, radius, blue, alpha) {
            const localX = Math.hypot(ux, uy), localY = Math.hypot(vx, vy);
            for (let i = 0; i < corners.length; i += 2) {
                const a = corners[i], b = corners[i+1];
                vertices[at++] = x + a * ux + b * vx; vertices[at++] = y + a * uy + b * vy;
                vertices[at++] = a * localX; vertices[at++] = b * localY; vertices[at++] = radius;
                vertices[at++] = (blue ? 166 : 228)/255; vertices[at++] = (blue ? 203 : 237)/255;
                vertices[at++] = 1; vertices[at++] = alpha;
            }
        }
        return {
            begin(width, height, ratio) {
                if (gpu.lost) return false;
                if (gpu.canvas.width !== Math.round(width * ratio) || gpu.canvas.height !== Math.round(height * ratio)) {
                    gpu.canvas.width = Math.round(width * ratio); gpu.canvas.height = Math.round(height * ratio);
                }
                at = 0; soft = .5 / ratio; gpu.gl.uniform2f(viewport, width, height); gpu.gl.uniform1f(feather, soft);
                gpu.gl.enable(gpu.gl.BLEND); gpu.gl.blendFunc(gpu.gl.ONE, gpu.gl.ONE_MINUS_SRC_ALPHA);
                gpu.gl.clearColor(0,0,0,0); gpu.gl.clear(gpu.gl.COLOR_BUFFER_BIT); return true;
            },
            star(x, y, radius, blue, alpha, tailX, tailY, lineWidth) {
                if (lineWidth) {
                    const dx = x - tailX, dy = y - tailY, length = Math.hypot(dx, dy), half = lineWidth / 2;
                    if (length) quad((x+tailX)/2, (y+tailY)/2, dx/2, dy/2, -dy/length*(half+soft), dx/length*(half+soft), -half, blue, alpha);
                }
                quad(x, y, radius+soft, 0, 0, radius+soft, radius, blue, alpha);
            },
            end(ctx, width, height) {
                if (gpu.draw(vertices.subarray(0, at), at / 9, true)) ctx.drawImage(gpu.canvas, 0, 0, width, height);
            }, dispose: () => gpu.dispose()
        };
    }
    window.alcyoneGpu = { create, stars };
})();
