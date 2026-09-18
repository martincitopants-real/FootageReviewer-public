/*
 * ExtendScript half of the FootageReviewer → Premiere bridge.
 *
 * Reads the handoff file FootageReviewer writes when you press C on a section, imports the source
 * recording(s) if they aren't in the project yet, trims each to the selected range, and drops them onto the
 * active sequence at the playhead.
 */

// ---- tiny helpers (ExtendScript is ES3: no JSON, no Array.indexOf, no trim) ----

function frReadFile(path) {
    var f = new File(path);
    if (!f.exists) return null;
    f.encoding = 'UTF-8';
    if (!f.open('r')) return null;
    var txt = f.read();
    f.close();
    return txt;
}

function frNormalize(p) {
    if (!p) return '';
    var s = String(p).replace(/\\/g, '/');
    return s.toLowerCase();
}

// Depth-first walk of the project tree looking for an already-imported clip with this media path.
function frFindItem(root, wantedPath) {
    for (var i = 0; i < root.children.numItems; i++) {
        var it = root.children[i];
        try {
            if (it.type === ProjectItemType.BIN) {
                var hit = frFindItem(it, wantedPath);
                if (hit) return hit;
            } else {
                var mp = it.getMediaPath ? it.getMediaPath() : '';
                if (mp && frNormalize(mp) === wantedPath) return it;
            }
        } catch (e) { /* some item types have no media path */ }
    }
    return null;
}

function frImport(path) {
    var before = app.project.rootItem.children.numItems;
    app.project.importFiles([path], true /*suppressUI*/, app.project.rootItem, false);
    // importFiles doesn't return the item, so look it up afterwards.
    var found = frFindItem(app.project.rootItem, frNormalize(path));
    if (found) return found;
    // Fall back to "the thing that just appeared".
    if (app.project.rootItem.children.numItems > before)
        return app.project.rootItem.children[app.project.rootItem.children.numItems - 1];
    return null;
}

/**
 * Main entry point, called from the panel.
 * @param {string} handoffPath  the JSON FootageReviewer wrote
 * @param {string} mode         'playhead' (default) or 'end'
 */
function frPasteSection(handoffPath, mode) {
    try {
        var txt = frReadFile(handoffPath);
        if (!txt) return 'ERR|No section found. Press C in FootageReviewer first.';

        var data = eval('(' + txt + ')'); // trusted local file we wrote ourselves
        if (!data || !data.pieces || !data.pieces.length) return 'ERR|That section was empty.';

        var seq = app.project.activeSequence;
        if (!seq) return 'ERR|Open a sequence in Premiere first.';

        // Where to drop it: the playhead, or after the last clip on V1.
        var at = 0;
        if (mode === 'end') {
            var v1 = seq.videoTracks[0];
            for (var t = 0; t < v1.clips.numItems; t++) {
                var end = v1.clips[t].end.seconds;
                if (end > at) at = end;
            }
        } else {
            at = seq.getPlayerPosition().seconds;
        }

        var placed = 0;
        for (var i = 0; i < data.pieces.length; i++) {
            var p = data.pieces[i];
            var item = frFindItem(app.project.rootItem, frNormalize(p.path));
            if (!item) item = frImport(p.path);
            if (!item) continue;

            // Trim the project item to the selected range, then lay it down.
            try {
                item.setInPoint(p.inSec, 4);   // 4 = video + audio
                item.setOutPoint(p.outSec, 4);
            } catch (e1) {
                try { item.setInPoint(p.inSec); item.setOutPoint(p.outSec); } catch (e2) { /* best effort */ }
            }

            try {
                seq.videoTracks[0].overwriteClip(item, at);
                placed++;
                at += (p.outSec - p.inSec);
            } catch (e3) { /* skip this piece, keep going */ }
        }

        if (placed === 0) return 'ERR|Could not place the clip (is the footage reachable?).';
        return 'OK|Placed ' + placed + ' clip' + (placed === 1 ? '' : 's') + ' at ' +
               (mode === 'end' ? 'the end of V1.' : 'the playhead.');
    } catch (err) {
        return 'ERR|' + err.toString();
    }
}

/** Timestamp of the handoff file, so the panel can auto-detect a fresh copy. */
function frHandoffStamp(handoffPath) {
    var f = new File(handoffPath);
    if (!f.exists) return '';
    try { return String(f.modified.getTime()); } catch (e) { return ''; }
}
