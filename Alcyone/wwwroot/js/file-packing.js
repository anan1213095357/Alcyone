(() => {
    const queue=[];let busy=false;
    const commits=new Map();
    function commitPackedFile(id){
        if(!id)return Promise.resolve(true);
        return new Promise(resolve=>{
            const timeout=setTimeout(()=>{commits.delete(id);resolve(false);},30000);
            commits.set(id,result=>{clearTimeout(timeout);resolve(result);});
            window.external?.sendMessage?.('alcyone:desktop:pack-commit:'+id);
        });
    }
    const personImage=new Image();personImage.src='/images/musk-packing-person.png';
    const sack='<svg viewBox="0 0 90 120"><path d="M28 10Q45 18 62 10L55 30Q89 66 78 103Q45 119 12 103Q1 67 35 30Z" fill="#967357" stroke="#d1b58b" stroke-width="2"/><path d="M30 30Q45 35 60 30 M23 56Q33 76 24 93 M58 48Q73 76 65 102" fill="none" stroke="#614b38" stroke-width="3"/></svg>';
    const ship='<div class="packing-original-rocket"><img src="/images/musk-packing-rocket.png" alt=""/></div><img class="packing-hugging-photo" src="/images/musk-hugging-starship.png" alt=""/><div class="packing-return-pose"><img src="/images/musk-hugging-starship.png" alt=""/></div>';

    const pose=(x,y,angle=0)=>`translate3d(${x}px,${y}px,0) rotate(${angle}deg)`;
    async function animate(node,frames,options){const animation=node.animate(frames,{fill:'forwards',...options});window.alcyoneAnimation?.track(animation);await animation.finished.catch(()=>{});}
    async function fly(rocket,start,end,departing=false){
        const dx=end.x-start.x,dy=end.y-start.y,distance=Math.hypot(dx,dy);
        const lift=Math.min(90,distance*.13),a={x:start.x+dx*.28,y:start.y+dy*.28-lift},b={x:end.x-dx*.22,y:end.y-dy*.22-lift};
        const frames=[];
        for(let i=0;i<=90;i++){
            const t=i/90,u=1-t,x=u*u*u*start.x+3*u*u*t*a.x+3*u*t*t*b.x+t*t*t*end.x,y=u*u*u*start.y+3*u*u*t*a.y+3*u*t*t*b.y+t*t*t*end.y;
            const vx=3*u*u*(a.x-start.x)+6*u*t*(b.x-a.x)+3*t*t*(end.x-b.x),vy=3*u*u*(a.y-start.y)+6*u*t*(b.y-a.y)+3*t*t*(end.y-b.y);
            let angle=Math.atan2(vy,vx)*180/Math.PI+90;if(angle>180)angle-=360;
            const blend=departing?Math.min(1,t/.22):Math.min(1,(1-t)/.22);
            frames.push({offset:t,transform:pose(x,y,angle*blend),opacity:departing?Math.min(1,(1-t)/.16):Math.min(1,t/.15)});
        }
        await animate(rocket,frames,{duration:Math.max(1100,Math.min(2300,distance*2.7)),easing:departing?'cubic-bezier(.4,0,.7,.6)':'cubic-bezier(.18,.65,.35,1)'});
    }
    async function jump(person,start,end){
        const frames=[];
        for(let i=0;i<=48;i++){
            const t=i/48,x=start.x+(end.x-start.x)*t,y=start.y+(end.y-start.y)*t-42*Math.sin(Math.PI*t);
            frames.push({offset:t,transform:pose(x,y,-7*Math.sin(Math.PI*t)),opacity:1});
        }
        await animate(person,frames,{duration:780,easing:'linear'});
        await animate(person.querySelector('.packing-person-rig'),[{transform:'translateY(0)'},{transform:'translateY(3px)',offset:.35},{transform:'translateY(0)'}],{duration:180,easing:'ease-out'});
    }
    function nearestPlanet(x,y){
        let nearest=null,distance=Infinity;
        for(const node of document.querySelectorAll('.alcyone-orb')){
            const r=node.getBoundingClientRect();if(!r.width||!r.height||r.right<0||r.bottom<0||r.left>innerWidth||r.top>innerHeight)continue;
            const p={x:r.left+r.width/2,y:r.top+r.height/2},d=Math.hypot(p.x-x,p.y-y);if(d<distance){distance=d;nearest=p;}
        }
        return nearest||{x:Math.max(50,innerWidth*.12),y:Math.max(80,innerHeight*.2)};
    }
    async function pack(item){
        const ratio=devicePixelRatio||1,x=Math.max(35,Math.min(innerWidth-35,item.x/ratio)),y=Math.max(60,Math.min(innerHeight-35,item.y/ratio)),origin=nearestPlanet(x,y);
        const layer=document.createElement('div');layer.className='file-packing-effect';
        const rocket=document.createElement('div');rocket.className='packing-ship';rocket.innerHTML=ship+'<i class="packing-ship-flame"></i><div class="packing-cargo">'+sack+'</div>';
        const person=document.createElement('div');person.className='packing-person';person.innerHTML='<div class="packing-person-facing"><div class="packing-person-sack">'+sack+'</div><div class="packing-person-rig">'+['leg-back','leg-front','body','arm-back','arm-front'].map(part=>'<img class="rig-'+part+'" src="'+personImage.src+'" alt=""/>').join('')+'</div></div>';
        const ring=document.createElement('div');ring.className='packing-launch-ring';Object.assign(ring.style,{left:(origin.x-24)+'px',top:(origin.y-24)+'px'});
        const parcel=document.createElement('div');parcel.className='packing-parcel';const icon=document.createElement('div');icon.className='packing-file';const name=document.createElement('span');name.textContent=item.name;icon.append(name);if(item.id)icon.style.visibility='hidden';parcel.append(icon);
        const wrap=document.createElement('div');wrap.className='packing-wrap';wrap.innerHTML='<i></i><b></b>';parcel.append(wrap);Object.assign(parcel.style,{left:(x-23)+'px',top:(y-25)+'px'});
        layer.append(ring,rocket,person,parcel);document.body.append(layer);
        const rx=Math.max(2,Math.min(innerWidth-125,x-150)),ry=Math.max(8,Math.min(innerHeight-185,y-170));
        const px=Math.max(rx+42,Math.min(innerWidth-90,x-87)),py=Math.max(8,Math.min(innerHeight-130,y-91));
        const initialFacing=x>=px+39 ? -1 : 1;
        person.querySelector('.packing-person-facing').style.transform=`scaleX(${initialFacing})`;
        const launchX=origin.x-60,launchY=origin.y-95,angle=origin.x>rx?-65:65;
        try{
            rocket.classList.add('flying');
            await fly(rocket,{x:launchX,y:launchY},{x:rx,y:ry});rocket.classList.remove('flying');
            const hugging=rocket.querySelector('.packing-hugging-photo'),bare=rocket.querySelector('.packing-original-rocket');
            // Exclusive ship layers: never draw the photographed and bare rocket together.
            hugging.style.visibility='hidden';bare.style.visibility='visible';
            await jump(person,{x:rx+42,y:ry+20},{x:px,y:py});
            await animate(person,[{transform:pose(px,py,0)},{transform:pose(px-7,py+9,-9)},{transform:pose(px,py,0)}],{duration:450,easing:'ease-in-out'});
            person.classList.add('is-packing');
            await animate(wrap,[{opacity:0,transform:'scale(.25)'},{opacity:1,transform:'scale(1.08)'},{opacity:1,transform:'scale(1)'}],{duration:650,easing:'ease-out'});
            await animate(icon,[{opacity:1,transform:'scale(1)'},{opacity:0,transform:'scale(.5)'}],{duration:160});
            const facing=person.querySelector('.packing-person-facing');
            if(initialFacing===-1) await animate(facing,[{transform:'scaleX(-1)'},{transform:'scaleX(-.3)',offset:.4},{transform:'scaleX(1)'}],{duration:350,easing:'ease-in-out'});
            const bagX=px+58,bagY=py+39;
            await Promise.all([
                animate(parcel,[{transform:'translate3d(0,0,0)',opacity:1},{transform:`translate3d(${(bagX-x)*.65}px,${bagY-y-35}px,0) rotate(-12deg) scale(.75)`,opacity:1,offset:.55},{transform:`translate3d(${bagX-x}px,${bagY-y}px,0) scale(.12)`,opacity:0}],{duration:900,easing:'ease-in-out'}),
                animate(person,[{transform:pose(px,py)},{transform:pose(px-5,py+7,-8),offset:.35},{transform:pose(px,py)}],{duration:900,easing:'ease-in-out'})
            ]);
            person.classList.remove('is-packing');
            const committed=await commitPackedFile(item.id);
            if(!committed)
            {
                wrap.classList.add('packing-cancelled');
                await animate(parcel,[{transform:`translate3d(${bagX-x}px,${bagY-y}px,0) scale(.2)`,opacity:0},{transform:'translate3d(0,0,0) scale(1)',opacity:1}],{duration:500,easing:'ease-out'});
                await animate(wrap,[{opacity:1},{opacity:0}],{duration:400});
            }
            await animate(person.querySelector('.packing-person-sack'),[{transform:'scale(1)'},{transform:'scale(1.16,.93)'},{transform:'scale(1.07)'}],{duration:300});
            person.classList.add('is-climbing');
            // Approach the hull, step up, then settle into the original hugging pose.
            await animate(person,[{transform:pose(px,py),opacity:1},{transform:pose(rx+44,ry+88,-3),opacity:1}],{duration:550,easing:'ease-in-out'});
            // Alternate hand/foot steps up the right side before settling into the photographed pose.
            await animate(person,[
                {transform:pose(rx+44,ry+88,-3),opacity:1,offset:0},
                {transform:pose(rx+42,ry+72,-6),opacity:1,offset:.22},
                {transform:pose(rx+44,ry+56,3),opacity:1,offset:.43},
                {transform:pose(rx+42,ry+40,-5),opacity:1,offset:.64},
                {transform:pose(rx+43,ry+24,2),opacity:1,offset:.85},
                {transform:pose(rx+42,ry+20,0),opacity:1,offset:1}
            ],{duration:1500,easing:'ease-in-out'});
            person.classList.remove('is-climbing');
            const returning=rocket.querySelector('.packing-return-pose');
            // Settle the hands and torso against the hull, then blend only the rider into the exact photo pose.
            await animate(person,[{transform:pose(rx+42,ry+20,0)},{transform:pose(rx+40,ry+18,-2)}],{duration:250,easing:'ease-out'});
            await Promise.all([
                animate(person,[{opacity:1},{opacity:0}],{duration:550,easing:'ease-in-out'}),
                animate(returning,[{opacity:0},{opacity:1}],{duration:550,easing:'ease-in-out'})
            ]);
            bare.style.visibility='hidden';hugging.style.visibility='visible';returning.style.visibility='hidden';
            rocket.querySelector('.packing-cargo').style.opacity=committed?'1':'0';rocket.classList.add('flying');
            await fly(rocket,{x:rx,y:ry},{x:launchX,y:launchY},true);
        }finally{layer.remove();}
    }
    async function pump(){if(busy)return;busy=true;try{while(queue.length)await pack(queue.shift());}finally{busy=false;}}
    window.external?.receiveMessage?.(message=>{
        if(typeof message!=='string')return;
        const resultPrefix='alcyone:desktop:pack-result:';
        if(message.startsWith(resultPrefix)){try{const result=JSON.parse(message.slice(resultPrefix.length));const resolve=commits.get(result.id);commits.delete(result.id);resolve?.(result.success===true);}catch{}return;}
        const prefix='alcyone:desktop:pack-file:';if(!message.startsWith(prefix))return;
        try{const item=JSON.parse(message.slice(prefix.length));if(!Number.isFinite(item.x)||!Number.isFinite(item.y))return;queue.push(item);pump().catch(()=>{});}catch{}
    });
})();
