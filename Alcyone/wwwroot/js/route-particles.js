(() => {
    let canvas,ctx,gpu,workspace,world,frame=0,routes=new Map(),seen=new Set();

    // Geometry is uploaded only when a route changes. Motion and interpolation stay on the GPU.
    function createGpu(canvas){
        const gl=canvas.getContext('webgl2',{alpha:true,antialias:false,premultipliedAlpha:true,preserveDrawingBuffer:false});
        if(!gl)return null;
        const program=gl.createProgram();
        for(const [type,source] of [[gl.VERTEX_SHADER,`#version 300 es
            precision highp float;
            uniform sampler2D lanes;
            uniform vec2 viewport,origin;
            uniform float scale,ratio,time,length,opacity;
            uniform int count;
            uniform bool active;
            out vec4 tint;
            out float pointRadius,pointDiameter;
            void main(){
                int lane=gl_VertexID/count, particle=gl_VertexID%count;
                float spacing=(lane<2||lane>6)?48.0:32.0;
                float distance=float(particle)*spacing+mod(time*(active?375.0:225.0)+float(lane)*19.0,spacing);
                float t=clamp(distance/max(length,0.001)*32.0,0.0,32.0);
                int i=min(31,int(floor(t)));
                vec2 p=mix(texelFetch(lanes,ivec2(i,lane),0).xy,texelFetch(lanes,ivec2(i+1,lane),0).xy,t-float(i));
                p=(p*scale+origin)/viewport*2.0-1.0;
                gl_Position=distance<length?vec4(p.x,-p.y,0,1):vec4(3,3,0,1);
                float radius=(lane==4?1.2:0.75)*(active?1.25:1.0)*ratio*scale;
                pointRadius=radius;pointDiameter=max(1.0,2.0*radius+2.0);gl_PointSize=pointDiameter;
                vec3 color=active?(lane%2==1?vec3(238,255,247):vec3(83,245,205)):(lane%2==1?vec3(212,234,255):vec3(136,191,255));
                tint=vec4(color/255.0,opacity*(active?0.9:0.65));
            }`],[gl.FRAGMENT_SHADER,`#version 300 es
            precision highp float;
            in vec4 tint;
            in float pointRadius,pointDiameter;
            out vec4 outputColor;
            void main(){
                float distance=length(gl_PointCoord-0.5)*2.0;
                float alpha=tint.a*(1.0-smoothstep(0.65,1.0,distance));
                outputColor=vec4(tint.rgb*alpha,alpha);
            }`]]){
            const shader=gl.createShader(type);gl.shaderSource(shader,source);gl.compileShader(shader);
            if(!gl.getShaderParameter(shader,gl.COMPILE_STATUS)){gl.deleteShader(shader);gl.deleteProgram(program);return null;}
            gl.attachShader(program,shader);gl.deleteShader(shader);
        }
        gl.linkProgram(program);
        if(!gl.getProgramParameter(program,gl.LINK_STATUS)){gl.deleteProgram(program);return null;}
        gl.useProgram(program);gl.enable(gl.BLEND);gl.blendFunc(gl.ONE,gl.ONE_MINUS_SRC_ALPHA);
        const uniforms=Object.fromEntries(['viewport','origin','scale','ratio','time','length','opacity','count','active'].map(n=>[n,gl.getUniformLocation(program,n)]));
        return {gl,program,uniforms};
    }
    function upload(route){
        if(!gpu||route.texture)return;
        const gl=gpu.gl,data=new Float32Array(33*9*2);
        route.lanes.forEach((lane,y)=>lane.forEach((p,x)=>{const i=(y*33+x)*2;data[i]=p.x;data[i+1]=p.y;}));
        route.texture=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,route.texture);
        gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.NEAREST);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.NEAREST);
        gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);
        gl.texImage2D(gl.TEXTURE_2D,0,gl.RG32F,33,9,0,gl.RG,gl.FLOAT,data);
    }
    function release(route){if(route?.texture&&gpu)gpu.gl.deleteTexture(route.texture);}

    function ensure(){
        if(canvas)return;
        workspace=document.getElementById('workspace');world=document.getElementById('world');if(!workspace||!world)return;
        canvas=document.createElement('canvas');canvas.className='route-particle-canvas';
        Object.assign(canvas.style,{position:'absolute',pointerEvents:'none',zIndex:'2'});
        world.append(canvas);gpu=createGpu(canvas);
        if(!gpu){canvas.remove();canvas=document.createElement('canvas');canvas.className='route-particle-canvas';Object.assign(canvas.style,{position:'absolute',pointerEvents:'none',zIndex:'2'});world.append(canvas);ctx=canvas.getContext('2d',{alpha:true});}
        canvas.addEventListener('webglcontextlost',event=>{event.preventDefault();if(frame)window.alcyoneAnimation.cancelFrame(frame);frame=0;});
        canvas.addEventListener('webglcontextrestored',()=>{for(const route of routes.values())route.texture=null;gpu=createGpu(canvas);if(!frame)frame=window.alcyoneAnimation.requestFrame(draw);});
        frame=window.alcyoneAnimation.requestFrame(draw);
    }
    function draw(time){
        frame=0;if(!canvas)return;
        if(!routes.size){if(gpu){gpu.gl.clear(gpu.gl.COLOR_BUFFER_BIT);}else ctx.clearRect(0,0,canvas.width,canvas.height);return;}
        const rect=workspace.getBoundingClientRect(),wr=world.getBoundingClientRect(),scale=wr.width/world.offsetWidth;
        const ratio=Math.min(2,devicePixelRatio||1),width=workspace.clientWidth,height=workspace.clientHeight;
        const w=Math.round(width*ratio),h=Math.round(height*ratio);
        if(canvas.width!==w||canvas.height!==h){canvas.width=w;canvas.height=h;}
        if(!(scale>0)){frame=window.alcyoneAnimation.requestFrame(draw);return;}
        const ox=wr.left-rect.left-workspace.clientLeft,oy=wr.top-rect.top-workspace.clientTop;
        const cssWidth=width/scale+'px',cssHeight=height/scale+'px';
        if(canvas.style.width!==cssWidth)canvas.style.width=cssWidth;if(canvas.style.height!==cssHeight)canvas.style.height=cssHeight;
        const x=-ox/scale+'px',y=-oy/scale+'px';
        if(canvas.style.left!==x)canvas.style.left=x;if(canvas.style.top!==y)canvas.style.top=y;
        if(gpu){const gl=gpu.gl;gl.viewport(0,0,w,h);gl.clear(gl.COLOR_BUFFER_BIT);gl.useProgram(gpu.program);}
        else {ctx.setTransform(1,0,0,1,0,0);ctx.clearRect(0,0,w,h);}
        if(scale>0 && routes.size){

            const left=-ox/scale,top=-oy/scale,right=left+width/scale,bottom=top+height/scale;
            if(!gpu)ctx.setTransform(ratio*scale,0,0,ratio*scale,ratio*ox,ratio*oy);
            else {const {gl,uniforms:u}=gpu;gl.uniform2f(u.viewport,width,height);gl.uniform2f(u.origin,ox,oy);gl.uniform1f(u.scale,scale);gl.uniform1f(u.ratio,ratio);gl.uniform1f(u.time,(time*.001)%32000);}
            for(const route of routes.values()){
                if(route.maxX<left||route.minX>right||route.maxY<top||route.minY>bottom||!route.opacity)continue;
                if(gpu){
                    upload(route);const {gl,uniforms:u}=gpu,count=Math.ceil(route.length/32)+1;
                    gl.bindTexture(gl.TEXTURE_2D,route.texture);gl.uniform1f(u.length,route.length);gl.uniform1f(u.opacity,route.opacity);gl.uniform1i(u.active,route.active?1:0);gl.uniform1i(u.count,count);
                    gl.drawArrays(gl.POINTS,0,count*9);continue;
                }
                const speed=route.active?375:225;
                for(let color=0;color<2;color++){
                    ctx.beginPath();
                    for(let lane=0;lane<9;lane++){
                        if(lane%2!==color)continue;
                        const points=route.lanes[lane],spacing=lane<2||lane>6?48:32;
                        const phase=(time*.001*speed+(lane*19))%spacing;
                        for(let distance=phase;distance<route.length;distance+=spacing){
                            const t=distance/route.length*32,index=Math.min(31,Math.floor(t)),f=t-index;
                            const a=points[index],b=points[index+1],x=a.x+(b.x-a.x)*f,y=a.y+(b.y-a.y)*f;
                            if(x<left-4||x>right+4||y<top-4||y>bottom+4)continue;
                            const size=(lane===4?1.2:.75)*(route.active?1.25:1);
                            ctx.moveTo(x+size,y);ctx.arc(x,y,size,0,Math.PI*2);
                        }
                    }
                    ctx.fillStyle=route.active?(color?'#eefff7':'#53f5cd'):(color?'#d4eaff':'#88bfff');ctx.globalAlpha=route.opacity*(route.active ? .9 : .65);ctx.fill();
                }
            }
        }
        if(ctx)ctx.globalAlpha=1;frame=window.alcyoneAnimation.requestFrame(draw);
    }
    window.alcyoneRouteParticles={
        begin(){ensure();seen.clear();},
        set(id,d,metric,opacity,active){
            seen.add(id);let route=routes.get(id);
            if(!route||route.d!==d){
                release(route);
                const length=metric.getTotalLength(),samples=[];
                for(let i=0;i<=32;i++){
                    const at=length*i/32,p=metric.getPointAtLength(at),a=metric.getPointAtLength(Math.max(0,at-1)),b=metric.getPointAtLength(Math.min(length,at+1));
                    const dx=b.x-a.x,dy=b.y-a.y,n=Math.max(.001,Math.hypot(dx,dy));samples.push({x:p.x,y:p.y,nx:-dy/n,ny:dx/n});
                }
                const lanes=[];for(let lane=-4;lane<=4;lane++)lanes.push(samples.map((p,i)=>{const jitter=(Math.sin(i*1.73+lane*3.1)+Math.sin(i*.63+lane))*(Math.abs(lane)>=3?3.2:1.2),shift=(lane*3.2+jitter)*Math.sin(Math.PI*i/32);return {x:p.x+p.nx*shift,y:p.y+p.ny*shift};}));
                route={d,length,lanes,minX:Math.min(...samples.map(p=>p.x))-24,maxX:Math.max(...samples.map(p=>p.x))+24,minY:Math.min(...samples.map(p=>p.y))-24,maxY:Math.max(...samples.map(p=>p.y))+24};routes.set(id,route);
            }
            route.opacity=opacity;route.active=active;
            if(canvas && !frame)frame=window.alcyoneAnimation.requestFrame(draw);
        },
        end(){for(const [id,route] of routes)if(!seen.has(id)){release(route);routes.delete(id);}},
        dispose(){if(frame)window.alcyoneAnimation.cancelFrame(frame);frame=0;for(const route of routes.values())release(route);if(gpu)gpu.gl.deleteProgram(gpu.program);gpu=null;canvas?.remove();canvas=null;ctx=null;routes.clear();}
    };
})();
