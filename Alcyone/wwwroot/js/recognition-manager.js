export function locateEntry(dialog, rootId, rowId) {
    if (!dialog) return;
    const entry = Array.from(dialog.querySelectorAll("[data-library-entry]"))
        .find(element => element.dataset.libraryEntry === rootId);
    entry?.scrollIntoView({ block: "nearest", inline: "nearest" });
    const row = Array.from(dialog.querySelectorAll("[data-recognition-row]"))
        .find(element => element.dataset.recognitionRow === rowId);
    row?.scrollIntoView({ block: "nearest", inline: "nearest" });
}
