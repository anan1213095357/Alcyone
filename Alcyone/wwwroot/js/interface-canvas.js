let cleanup;
export function attach(bridge) {
    cleanup?.();
    const abort=new AbortController(),signal=abort.signal;
    let drag=null,frame=0;
    const position=e=>{const world=document.getElementById('world'),r=world.getBoundingClientRect(),scale=r.width/world.offsetWidth;return {x:(e.clientX-r.left)/scale,y:(e.clientY-r.top)/scale,scale};};
    document.addEventListener('dragstart',e=>{const source=e.target.closest('[data-interface-source]');if(source)e.dataTransfer.setData('application/alcyone-interface',source.dataset.interfaceSource);},{signal});
    document.addEventListener('dragover',e=>{if(e.target.closest('#workspace')&&!e.target.closest('.interface-designer')&&Array.from(e.dataTransfer.types).includes('application/alcyone-interface')){e.preventDefault();e.dataTransfer.dropEffect='copy';}},{signal});
    document.addEventListener('drop',e=>{const id=e.dataTransfer.getData('application/alcyone-interface');if(!id||!e.target.closest('#workspace')||e.target.closest('.interface-designer'))return;e.preventDefault();const p=position(e);bridge.invokeMethodAsync('PlaceInterface',document.querySelector('[data-interface-config]')?.dataset.interfaceConfig||'',id,p.x,p.y).catch(()=>{});},{signal});
    const draw=()=>{frame=0;if(!drag)return;drag.x=Math.max(0,Math.min(Math.max(0,4000-drag.node.offsetWidth),drag.left+(drag.cx-drag.sx)/drag.scale));drag.y=Math.max(0,Math.min(Math.max(0,2500-drag.node.offsetHeight),drag.top+(drag.cy-drag.sy)/drag.scale));drag.node.style.transform=`translate3d(${drag.x-drag.left}px,${drag.y-drag.top}px,0)`;};
    document.addEventListener('pointerdown',e=>{
        const input=e.target.closest('.interface-surface input[data-native-input]');
        if(input && e.button===0 && window.external?.sendMessage) {
            e.preventDefault();
            const r=input.getBoundingClientRect(),style=getComputedStyle(input),parent=getComputedStyle(input.parentElement);
            window.external.sendMessage('alcyone:desktop:text-input:'+JSON.stringify({id:input.dataset.nativeInput,text:input.value,left:r.left+parseFloat(style.paddingLeft),top:r.top+1,width:Math.max(1,r.width-parseFloat(style.paddingLeft)-parseFloat(style.paddingRight)),height:r.height-2,ratio:devicePixelRatio||1,fontSize:parseFloat(style.fontSize),fontFamily:style.fontFamily.split(',')[0].replaceAll('"',''),textColor:parent.color,backgroundColor:parent.backgroundColor,valueKind:input.dataset.valueKind,transparentSurface:true}));
            return;
        }
        const handle=e.target.closest('[data-interface-handle]');if(!handle||e.target.closest('button')||e.button!==0)return;e.preventDefault();e.stopPropagation();const node=handle.closest('.interface-surface'),p=position(e);drag={node,left:node.offsetLeft,top:node.offsetTop,sx:e.clientX,sy:e.clientY,cx:e.clientX,cy:e.clientY,scale:p.scale,pointer:e.pointerId};node.setPointerCapture(e.pointerId);},{signal});
    document.addEventListener('pointermove',e=>{if(!drag||e.pointerId!==drag.pointer)return;drag.cx=e.clientX;drag.cy=e.clientY;if(!frame)frame=requestAnimationFrame(draw);},{signal});
    const finish=e=>{if(!drag||e.pointerId!==drag.pointer)return;cancelAnimationFrame(frame);draw();const d=drag;drag=null;d.node.style.left=d.x+'px';d.node.style.top=d.y+'px';d.node.style.transform='';bridge.invokeMethodAsync('PlaceInterface',d.node.dataset.configKey,d.node.dataset.interfaceId,d.x,d.y).catch(()=>{});};
    document.addEventListener('pointerup',finish,{signal});document.addEventListener('pointercancel',finish,{signal});
    cleanup=()=>{abort.abort();cancelAnimationFrame(frame);};
}
export function detach(){cleanup?.();cleanup=null;}
