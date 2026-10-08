window.fcfCanvas = (() => {

    let viewport;
    let overlay;
    let image;
    let magnifier;
    let magnifierImage;
    let magnifierText;

    let dotnet;
    let frameUrl;

    let width = 0;
    let height = 0;

    let points = [];

    let selected = -1;
    let dragging = -1;

    let timer = null;
    let busy = false;
    let disposed = false;

    let frameObjectUrl = null;

    let mouse = {
        x: 0,
        y: 0,
        inside: false
    };


    // 仅用于读取鼠标下面 RGB。
    // 不是显示 UI。
    const sampleCanvas = document.createElement("canvas");
    sampleCanvas.width = 1;
    sampleCanvas.height = 1;

    const sampleContext =
        sampleCanvas.getContext("2d", {
            willReadFrequently: true
        });


    function init(id, dotnetRef, url) {

        dispose();

        disposed = false;

        viewport =
            document.getElementById(id);

        if (!viewport)
            return;

        overlay =
            document.getElementById("annotationOverlay");

        image =
            document.getElementById("annotationImage");

        magnifier =
            document.getElementById("pixelMagnifier");

        magnifierImage =
            document.getElementById("magnifierImage");

        magnifierText =
            document.getElementById("magnifierText");

        dotnet = dotnetRef;
        frameUrl = url;


        overlay.addEventListener(
            "pointerdown",
            onPointerDown);

        overlay.addEventListener(
            "pointermove",
            onPointerMove);

        overlay.addEventListener(
            "pointerup",
            onPointerUp);

        overlay.addEventListener(
            "pointerleave",
            onPointerLeave);

        overlay.addEventListener(
            "contextmenu",
            onContextMenu);

        viewport.addEventListener(
            "keydown",
            onKeyDown);


        window.addEventListener(
            "resize",
            layout);


        schedule(10);
    }


    function setState(w, h, newPoints) {

        width = Math.max(
            0,
            w | 0);

        height = Math.max(
            0,
            h | 0);


        points =
            Array.isArray(newPoints)
                ? newPoints.map(p => ({
                    x: p.x ?? p.X,
                    y: p.y ?? p.Y
                }))
                : [];


        selected = Math.min(
            selected,
            points.length - 1);


        layout();

        renderPoints();
    }


    function layout() {

        if (!viewport ||
            width <= 0 ||
            height <= 0)
            return;


        const stage =
            viewport.parentElement;

        if (!stage)
            return;


        const availableWidth =
            Math.max(
                1,
                stage.clientWidth - 20);

        const availableHeight =
            Math.max(
                1,
                stage.clientHeight - 20);


        const scale =
            Math.min(
                availableWidth / width,
                availableHeight / height);


        const displayWidth =
            Math.max(
                1,
                Math.round(width * scale));

        const displayHeight =
            Math.max(
                1,
                Math.round(height * scale));


        viewport.style.width =
            displayWidth + "px";

        viewport.style.height =
            displayHeight + "px";


        renderPoints();
    }


    function schedule(ms = 35) {

        if (disposed)
            return;

        clearTimeout(timer);

        timer =
            setTimeout(
                updateFrame,
                ms);
    }


    async function updateFrame() {

        if (disposed ||
            busy ||
            width <= 0 ||
            height <= 0) {

            schedule(80);
            return;
        }


        busy = true;

        try {

            const response =
                await fetch(
                    frameUrl +
                    "?t=" +
                    performance.now(),
                    {
                        cache: "no-store"
                    });


            if (!response.ok ||
                response.status === 204)
                return;


            const blob =
                await response.blob();


            const oldUrl =
                frameObjectUrl;


            const newUrl =
                URL.createObjectURL(blob);


            frameObjectUrl =
                newUrl;


            image.src =
                newUrl;

            magnifierImage.src =
                newUrl;


            try {

                await image.decode();

            } catch {
            }


            if (oldUrl) {

                URL.revokeObjectURL(
                    oldUrl);
            }


            layout();

        } catch {
        }
        finally {

            busy = false;

            schedule(35);
        }
    }


    // =====================================================
    // DIV 标注
    // =====================================================

    function renderPoints() {

        if (!overlay ||
            width <= 0 ||
            height <= 0)
            return;


        overlay.innerHTML = "";


        for (let i = 0;
            i < points.length;
            i++) {

            const p =
                points[i];


            const marker =
                document.createElement(
                    "div");


            marker.className =
                "point-marker";


            if (i === 0)
                marker.classList.add(
                    "anchor");


            if (i === selected)
                marker.classList.add(
                    "selected");


            marker.dataset.index =
                i.toString();


            marker.style.left =
                ((p.x / width) * 100) +
                "%";


            marker.style.top =
                ((p.y / height) * 100) +
                "%";


            const label =
                document.createElement(
                    "div");


            label.className =
                "point-label";


            label.textContent =
                i === 0
                    ? "A  锚点"
                    : `P${i + 1}`;


            marker.appendChild(
                label);


            overlay.appendChild(
                marker);
        }
    }


    // =====================================================
    // 坐标
    // =====================================================

    function eventPoint(ev) {

        const rect =
            overlay.getBoundingClientRect();


        return {

            x: clamp(
                (ev.clientX - rect.left) *
                width /
                rect.width,

                0,
                width - 1),

            y: clamp(
                (ev.clientY - rect.top) *
                height /
                rect.height,

                0,
                height - 1)
        };
    }


    // =====================================================
    // 鼠标
    // =====================================================

    function onPointerDown(ev) {

        if (ev.button !== 0 ||
            width <= 0)
            return;


        viewport.focus();


        const marker =
            ev.target.closest(
                ".point-marker");


        if (marker) {

            selected =
                Number(
                    marker.dataset.index);

            dragging =
                selected;

        } else {

            const p =
                eventPoint(ev);


            points.push({

                x: Math.round(p.x),

                y: Math.round(p.y)
            });


            selected =
                points.length - 1;


            dragging =
                selected;


            notify();
        }


        overlay.setPointerCapture(
            ev.pointerId);


        renderPoints();
    }


    function onPointerMove(ev) {

        if (width <= 0)
            return;


        const p =
            eventPoint(ev);


        mouse = {

            x: p.x,

            y: p.y,

            inside: true
        };


        if (dragging >= 0) {

            points[dragging] = {

                x: Math.round(p.x),

                y: Math.round(p.y)
            };


            renderPoints();
        }


        updateMagnifier();
    }


    function onPointerUp(ev) {

        if (dragging >= 0)
            notify();


        dragging = -1;


        try {

            overlay.releasePointerCapture(
                ev.pointerId);

        } catch {
        }


        renderPoints();
    }


    function onPointerLeave() {

        mouse.inside = false;

        hideMagnifier();
    }


    function onContextMenu(ev) {

        ev.preventDefault();


        const marker =
            ev.target.closest(
                ".point-marker");


        if (!marker)
            return;


        const index =
            Number(
                marker.dataset.index);


        points.splice(
            index,
            1);


        if (selected === index)
            selected = -1;

        else if (selected > index)
            selected--;


        notify();

        renderPoints();
    }


    // =====================================================
    // 键盘微调
    // =====================================================

    function onKeyDown(ev) {

        if (selected < 0 ||
            selected >= points.length)
            return;


        const distance =
            ev.shiftKey
                ? 5
                : 1;


        let p = {
            ...points[selected]
        };


        let handled = true;


        switch (ev.key) {

            case "ArrowLeft":
                p.x -= distance;
                break;

            case "ArrowRight":
                p.x += distance;
                break;

            case "ArrowUp":
                p.y -= distance;
                break;

            case "ArrowDown":
                p.y += distance;
                break;

            case "Delete":

                points.splice(
                    selected,
                    1);

                selected = -1;

                notify();

                renderPoints();

                ev.preventDefault();

                return;

            default:
                handled = false;
                break;
        }


        if (!handled)
            return;


        p.x =
            clamp(
                Math.round(p.x),
                0,
                width - 1);


        p.y =
            clamp(
                Math.round(p.y),
                0,
                height - 1);


        points[selected] =
            p;


        notify();

        renderPoints();

        ev.preventDefault();
    }


    // =====================================================
    // DIV 放大镜
    // =====================================================

    function updateMagnifier() {

        if (!mouse.inside ||
            !image.complete ||
            width <= 0 ||
            height <= 0) {

            hideMagnifier();
            return;
        }


        magnifier.classList.remove(
            "hidden");


        const rect =
            viewport.getBoundingClientRect();


        const screenX =
            mouse.x /
            width *
            rect.width;


        const screenY =
            mouse.y /
            height *
            rect.height;


        const magWidth =
            magnifier.offsetWidth || 166;


        const magHeight =
            magnifier.offsetHeight || 190;


        let left =
            screenX + 22;


        let top =
            screenY + 22;


        if (left + magWidth >
            rect.width - 8) {

            left =
                screenX -
                magWidth -
                22;
        }


        if (top + magHeight >
            rect.height - 8) {

            top =
                screenY -
                magHeight -
                22;
        }


        left =
            clamp(
                left,
                8,
                Math.max(
                    8,
                    rect.width -
                    magWidth -
                    8));


        top =
            clamp(
                top,
                8,
                Math.max(
                    8,
                    rect.height -
                    magHeight -
                    8));


        magnifier.style.left =
            left + "px";


        magnifier.style.top =
            top + "px";


        const sample =
            15;


        const zoom =
            10;


        const half =
            Math.floor(
                sample / 2);


        const sx =
            clamp(
                Math.round(mouse.x) -
                half,

                0,

                Math.max(
                    0,
                    width - sample));


        const sy =
            clamp(
                Math.round(mouse.y) -
                half,

                0,

                Math.max(
                    0,
                    height - sample));


        magnifierImage.style.width =
            width * zoom +
            "px";


        magnifierImage.style.height =
            height * zoom +
            "px";


        magnifierImage.style.left =
            -(sx * zoom) +
            "px";


        magnifierImage.style.top =
            -(sy * zoom) +
            "px";


        const rgb =
            readPixel(
                Math.round(mouse.x),
                Math.round(mouse.y));


        magnifierText.textContent =
            rgb
                ? `X ${Math.round(mouse.x)}   Y ${Math.round(mouse.y)}   RGB ${rgb.r}, ${rgb.g}, ${rgb.b}`
                : `X ${Math.round(mouse.x)}   Y ${Math.round(mouse.y)}`;
    }


    function readPixel(x, y) {

        try {

            sampleContext.clearRect(
                0,
                0,
                1,
                1);


            sampleContext.drawImage(
                image,
                x,
                y,
                1,
                1,
                0,
                0,
                1,
                1);


            const data =
                sampleContext.getImageData(
                    0,
                    0,
                    1,
                    1).data;


            return {

                r: data[0],

                g: data[1],

                b: data[2]
            };

        } catch {

            return null;
        }
    }


    function hideMagnifier() {

        if (magnifier)
            magnifier.classList.add(
                "hidden");
    }


    // =====================================================
    // C#
    // =====================================================

    function notify() {

        if (!dotnet)
            return;


        dotnet
            .invokeMethodAsync(
                "OnPointsChanged",

                points.map(p => ({

                    x: p.x | 0,

                    y: p.y | 0
                })))

            .catch(() => {
            });
    }


    function clamp(
        value,
        min,
        max) {

        return Math.max(
            min,
            Math.min(
                max,
                value));
    }


    function dispose() {

        disposed = true;


        clearTimeout(timer);

        timer = null;


        window.removeEventListener(
            "resize",
            layout);


        if (frameObjectUrl) {

            URL.revokeObjectURL(
                frameObjectUrl);

            frameObjectUrl = null;
        }


        viewport =
            overlay =
            image =
            magnifier =
            magnifierImage =
            magnifierText =
            dotnet =
            null;
    }


    return {

        init,

        setState,

        dispose
    };

})();