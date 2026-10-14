(() => {
    let canvas,ctx,workspace,world,frame=0,routes=new Map(),seen=new Set();
    function ensure(){
        if(canvas)return;
        workspace=document.getElementById('workspace');world=document.getElementById('world');if(!workspace||!world)return;
        canvas=document.createElement('canvas');canvas.className='route-particle-canvas';
        Object.assign(canvas.style,{position:'absolute',pointerEvents:'none',zIndex:'2'});
        workspace.append(canvas);ctx=canvas.getContext('2d',{alpha:true});
        frame=window.alcyoneAnimation.requestFrame(draw);
    }
    function draw(time){
        frame=0;if(!canvas)return;
        if(!routes.size){ctx.clearRect(0,0,canvas.width,canvas.height);return;}
        const rect=workspace.getBoundingClientRect(),wr=world.getBoundingClientRect(),scale=wr.width/world.offsetWidth;
        const ratio=Math.min(2,devicePixelRatio||1),width=workspace.clientWidth,height=workspace.clientHeight;
        const w=Math.round(width*ratio),h=Math.round(height*ratio);
        if(canvas.width!==w||canvas.height!==h){canvas.width=w;canvas.height=h;canvas.style.width=width+'px';canvas.style.height=height+'px';}
        const x=workspace.scrollLeft+'px',y=workspace.scrollTop+'px';
        if(canvas.style.left!==x)canvas.style.left=x;if(canvas.style.top!==y)canvas.style.top=y;
        ctx.setTransform(1,0,0,1,0,0);ctx.clearRect(0,0,w,h);
        if(scale>0 && routes.size){
            const ox=wr.left-rect.left-workspace.clientLeft,oy=wr.top-rect.top-workspace.clientTop;
            const left=-ox/scale,top=-oy/scale,right=left+width/scale,bottom=top+height/scale;
            ctx.setTransform(ratio*scale,0,0,ratio*scale,ratio*ox,ratio*oy);
            for(const route of routes.values()){
                if(route.maxX<left||route.minX>right||route.maxY<top||route.minY>bottom||!route.opacity)continue;
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
        ctx.globalAlpha=1;frame=window.alcyoneAnimation.requestFrame(draw);
    }
    window.alcyoneRouteParticles={
        begin(){ensure();seen.clear();},
        set(id,d,metric,opacity,active){
            seen.add(id);let route=routes.get(id);
            if(!route||route.d!==d){
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
        end(){for(const id of routes.keys())if(!seen.has(id))routes.delete(id);},
        dispose(){if(frame)window.alcyoneAnimation.cancelFrame(frame);frame=0;canvas?.remove();canvas=null;ctx=null;routes.clear();}
    };
})();
