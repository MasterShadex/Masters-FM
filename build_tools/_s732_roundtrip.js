// Stage 7.32 STEP 3 SE1 -- DECISIVE import/export round-trips, DOM-free.
// Extracts the REAL functions from customize.html (deepMerge / flatToNested /
// nestedToFlat / getNestedAtPath / isValidPresetShape / FLAT_TO_NESTED_MAP) and
// simulates the export/import pipeline (State.nested passthrough + deepMerge).
//   T1 new-panel export -> import == lossless INCLUDING layout geometry
//   T2 a reconstructed V1-format (friend v12) preset imports + applies correctly
//   T3 malformed / wrong-shape rejected (validation preserved)
// Run: node build_tools/_s732_roundtrip.js
const fs = require('fs');
const html = fs.readFileSync('G:/Project Folder/Master FM/src/customize.html', 'utf8');

function balancedFrom(startIdx) {
    const bs = html.indexOf('{', startIdx);
    let depth = 0, i = bs;
    for (; i < html.length; i++) { const c = html[i]; if (c === '{') depth++; else if (c === '}') { depth--; if (depth === 0) { i++; break; } } }
    return html.slice(startIdx, i);
}
function fn(sig) { const idx = html.indexOf(sig); if (idx < 0) throw new Error('not found: ' + sig); return balancedFrom(idx); }
function constObj(name) { const idx = html.indexOf('const ' + name + ' ='); if (idx < 0) throw new Error('no ' + name); return balancedFrom(idx) + ';'; }

const src = [
    constObj('FLAT_TO_NESTED_MAP'),
    fn('function deepMerge('),
    fn('function flatToNested('),
    fn('function getNestedAtPath('),
    fn('function nestedToFlat('),
    fn('function isValidPresetShape(')
].join('\n');

const M = (new Function(src + '\n; return { deepMerge, flatToNested, nestedToFlat, getNestedAtPath, isValidPresetShape, FLAT_TO_NESTED_MAP };'))();

let fail = 0;
const must = (c, m) => { console.log((c ? 'PASS  ' : 'FAIL  ') + m); if (!c) fail++; };
function deepEqual(a, b) {
    if (a === b) return true;
    if (typeof a !== typeof b) return false;
    if (a && b && typeof a === 'object') {
        if (Array.isArray(a) !== Array.isArray(b)) return false;
        if (Array.isArray(a)) { if (a.length !== b.length) return false; return a.every((x, i) => deepEqual(x, b[i])); }
        const ka = Object.keys(a), kb = Object.keys(b);
        if (ka.length !== kb.length) return false;
        return ka.every(k => Object.prototype.hasOwnProperty.call(b, k) && deepEqual(a[k], b[k]));
    }
    return false;
}

// A realistic server-loaded nested config WITH layout geometry + unmapped keys
// (lastPresetName) -- exactly the data the flat-only V2 pipeline used to drop.
const serverNested = {
    font: 'Inter',
    masters: { accentColor: '#c060ff', overallSize: 100, textSize: 100, glowEnabled: true, animationsEnabled: true },
    card: { borderRadius: 56, borderThickness: 5, backgroundTop: 'rgba(18,6,36,0.99)', backgroundBottom: 'rgba(12,4,24,0.99)', backgroundAngle: 148, backgroundBlur: 0, backgroundOpacity: 0.99 },
    border: { enabled: true, spinDuration: 4, colors: ['#ff1085', '#ff60c8', '#d040ff', '#8020e0', '#4a0ab8'] },
    title: { fontSize: 68, fontWeight: 800, color: '#ffffff', marqueeSpeed: 68, marqueePause: 2, glowEnabled: false, glowColor: '#ff80ff', glowSize: 6, letterSpacing: 0 },
    spectrum: { enabled: true, barCount: 50, gap: 3, barRadius: 4, colorMode: 'rainbow', color: '#c060ff', smoothing: 0.6, mirrorMode: false, heightMult: 1, minHeight: 2, opacity: 1, fps: 120, autoGain: false, responseMs: 10.7, sensitivity: 1 },
    progressBar: { enabled: true, height: 9, trackColor: 'rgba(160,70,255,0.10)', fillColors: ['#8020c0', '#ff60c8', '#c040ff'], borderRadius: 6 },
    lastPresetName: '',
    layout: {
        enabled: true, canvas: { width: 1000, height: 200 }, gridSize: 8, nodes: {
            albumArt: { x: 0, y: 0, w: 135, h: 135, z: 1, visible: true, locked: false, anchor: 'topleft' },
            spectrum: { x: 18, y: 12, w: 290, h: 75, z: 5, visible: true, locked: false, anchor: 'topright' },
            trackTitle: { x: 155, y: 38, w: 480, h: 38, z: 3, visible: true, locked: false, anchor: 'topleft' }
        }
    }
};

// ---- T1: new-panel export -> new-panel import, lossless incl layout ----
console.log('\n=== T1: new->new round-trip (lossless incl layout) ===');
const A = { nested: M.deepMerge({}, serverNested), config: M.nestedToFlat(serverNested), isDirty: false };
// user tweaks a few controls of every interesting kind
A.config['c-card-radius'] = 42;
A.config['c-title-color'] = '#ff00ff';
A.config['c-spec-bars'] = 480;
A.config['c-border-colors'] = '#aabbcc,#ddeeff,#102030';
A.config['c-master-glow'] = false;
const export1 = M.deepMerge(A.nested, M.flatToNested(A.config));
must(export1.layout && export1.layout.nodes && export1.layout.nodes.spectrum.x === 18, 'export carries layout.nodes geometry (spectrum.x=18) -- NOT dropped');
must(export1.layout.enabled === true && export1.layout.canvas.width === 1000, 'export carries layout.enabled + canvas');
must(export1.card.borderRadius === 42 && export1.title.color === '#ff00ff', 'export reflects tweaked control values');
must(export1.spectrum.barCount === 480, 'export reflects tweaked slider (spec bars 480)');
must(deepEqual(export1.border.colors, ['#aabbcc', '#ddeeff', '#102030']), 'export border.colors is array (comma-string -> array)');
must(export1.lastPresetName === '', 'export carries unmapped key lastPresetName');
// fresh panel imports export1 (same baseline)
const B = { nested: M.deepMerge({}, serverNested), config: M.nestedToFlat(serverNested), isDirty: false };
const cand1 = export1;
B.nested = M.deepMerge(B.nested, cand1);
B.config = Object.assign({}, B.config, M.nestedToFlat(cand1));
const export2 = M.deepMerge(B.nested, M.flatToNested(B.config));
must(deepEqual(export1, export2), 'export1 === export2 (full round-trip lossless, deep-equal)');
must(deepEqual(export2.layout, export1.layout), 'layout subtree identical after round-trip (canvas + all nodes)');

// ---- T2: a friend's V1-format (v9.6.3/v12) preset imports + applies ----
console.log('\n=== T2: V1-format friend preset import + apply ===');
const friend = {
    format: 'mastersfm.preset', version: 1, name: 'Friend Layout', exportedAt: '2026-01-01T00:00:00Z', exportedFrom: 'v9.6.3',
    config: {
        font: 'Space Grotesk',
        card: { borderRadius: 20, backgroundTop: '#111111' },
        title: { color: '#37ff00', fontSize: 72 },
        spectrum: { barCount: 480, colorMode: 'gradient' },
        border: { colors: ['#111', '#222', '#333', '#444', '#555'] },
        layout: {
            enabled: true, canvas: { width: 1000, height: 200 }, gridSize: 8, nodes: {
                albumArt: { x: 5, y: 5, w: 120, h: 120, z: 1, visible: true, locked: true, anchor: 'topleft' },
                spectrum: { x: 0, y: 0, w: 420, h: 135, z: 5, visible: true, locked: true, anchor: 'topright' }
            }
        }
    }
};
// import detection (mirror handleImportFile): envelope OR bare config
const cand2 = (friend && friend.format === 'mastersfm.preset' && friend.config) ? friend.config : friend;
must(M.isValidPresetShape(cand2) === true, 'friend envelope.config passes isValidPresetShape');
const C = { nested: M.deepMerge({}, serverNested), config: M.nestedToFlat(serverNested), isDirty: false };
C.nested = M.deepMerge(C.nested, cand2);
C.config = Object.assign({}, C.config, M.nestedToFlat(cand2));
const eff = M.deepMerge(C.nested, M.flatToNested(C.config));
// The friend preset specified albumArt + spectrum (partial); deepMerge applies
// those EXACTLY and preserves baseline-only nodes (trackTitle) rather than
// blanking them -- same "fill missing from baseline" philosophy V1 used. A real
// V1 export is complete (all 8 nodes), so T1's exact round-trip is the full case.
must(eff.layout.enabled === true && deepEqual(eff.layout.canvas, cand2.layout.canvas) && eff.layout.gridSize === 8, "friend's layout enabled/canvas/gridSize applied");
must(deepEqual(eff.layout.nodes.albumArt, cand2.layout.nodes.albumArt), "friend's albumArt node applied exactly (incl locked:true)");
must(deepEqual(eff.layout.nodes.spectrum, cand2.layout.nodes.spectrum), "friend's spectrum node applied exactly (incl locked:true)");
must(eff.layout.nodes.trackTitle && eff.layout.nodes.trackTitle.x === 155, 'baseline-only node (trackTitle) preserved, not blanked');
must(eff.card.borderRadius === 20 && eff.card.backgroundTop === '#111111', 'friend card values applied');
must(eff.title.color === '#37ff00' && eff.title.fontSize === 72, 'friend title values applied');
must(eff.spectrum.barCount === 480 && eff.spectrum.colorMode === 'gradient', 'friend spectrum values applied');
must(deepEqual(eff.border.colors, ['#111', '#222', '#333', '#444', '#555']), 'friend border.colors applied');
must(eff.font === 'Space Grotesk', 'friend font applied');
// flats the panel renders got set
must(C.config['c-card-radius'] === 20 && C.config['c-title-color'] === '#37ff00', 'flat controls updated from friend import');
must(C.config['c-border-colors'] === '#111,#222,#333,#444,#555', 'border colors flat = comma-string');
// keys the friend did NOT override survive from baseline (no blanking)
must(eff.progressBar && eff.progressBar.height === 9, 'baseline progressBar survives (unset keys not blanked)');
must(eff.masters && eff.masters.accentColor === '#c060ff', 'baseline masters survive');

// ---- T3: malformed / wrong-shape rejected ----
console.log('\n=== T3: validation (malformed rejected) ===');
must(M.isValidPresetShape(null) === false, 'reject null');
must(M.isValidPresetShape([]) === false, 'reject array');
must(M.isValidPresetShape({}) === false, 'reject empty object');
must(M.isValidPresetShape({ foo: 1, bar: 2 }) === false, 'reject object with no valid top key');
must(M.isValidPresetShape('a string') === false, 'reject string');
must(M.isValidPresetShape(42) === false, 'reject number');
must(M.isValidPresetShape(undefined) === false, 'reject undefined');
must(M.isValidPresetShape({ card: { borderRadius: 1 } }) === true, 'accept bare config with a valid top key (card)');
must(M.isValidPresetShape({ layout: { enabled: true } }) === true, 'accept a layout-bearing config (layout is a valid top key)');

// ---- Evidence (S3.3) ----
const evidence = {
    stage: '7.32 STEP 3 -- import/export V1-compatible + lossless',
    method: 'DOM-free: real deepMerge/flatToNested/nestedToFlat/isValidPresetShape extracted from customize.html',
    summary: fail === 0 ? 'ALL PASS' : (fail + ' FAILED'),
    fail_count: fail,
    T1_new_to_new_lossless_incl_layout: {
        export_carries_layout_node_geometry: export1.layout.nodes.spectrum.x === 18,
        export_carries_layout_enabled_canvas: export1.layout.enabled === true && export1.layout.canvas.width === 1000,
        export_carries_unmapped_key_lastPresetName: export1.lastPresetName === '',
        export1_equals_export2_deep: deepEqual(export1, export2),
        layout_subtree_identical: deepEqual(export2.layout, export1.layout),
        tweaked_values_in_export: { cardRadius: export1.card.borderRadius, titleColor: export1.title.color, specBars: export1.spectrum.barCount, borderColors: export1.border.colors }
    },
    T2_v1_format_friend_import: {
        envelope_detected_and_valid: M.isValidPresetShape(cand2),
        friend_layout_enabled: eff.layout.enabled,
        friend_albumArt_applied: deepEqual(eff.layout.nodes.albumArt, cand2.layout.nodes.albumArt),
        friend_spectrum_applied: deepEqual(eff.layout.nodes.spectrum, cand2.layout.nodes.spectrum),
        baseline_only_node_preserved: eff.layout.nodes.trackTitle && eff.layout.nodes.trackTitle.x === 155,
        friend_card_radius: eff.card.borderRadius, friend_title_color: eff.title.color,
        friend_spec_bars: eff.spectrum.barCount, friend_font: eff.font,
        flat_card_radius: C.config['c-card-radius'], flat_border_colors: C.config['c-border-colors'],
        baseline_progressBar_survives: !!(eff.progressBar && eff.progressBar.height === 9)
    },
    T3_validation_rejects: {
        null: M.isValidPresetShape(null), array: M.isValidPresetShape([]), empty: M.isValidPresetShape({}),
        no_valid_key: M.isValidPresetShape({ foo: 1 }), string: M.isValidPresetShape('x'), number: M.isValidPresetShape(42),
        accept_card: M.isValidPresetShape({ card: { borderRadius: 1 } }), accept_layout: M.isValidPresetShape({ layout: { enabled: true } })
    }
};
try { fs.mkdirSync('G:/Project Folder/Master FM/evidence/s7_32', { recursive: true }); } catch (e) {}
fs.writeFileSync('G:/Project Folder/Master FM/evidence/s7_32/import_export_compat.json', JSON.stringify(evidence, null, 2));
console.log('\nevidence -> evidence/s7_32/import_export_compat.json');
console.log('\n' + (fail === 0 ? 'ALL ROUND-TRIP CHECKS PASS' : fail + ' CHECK(S) FAILED'));
process.exit(fail === 0 ? 0 : 1);
