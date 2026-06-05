// Stage 7.32 STEP 1 SE1 verifier (DOM-free). Read-only check of customize.html:
//  (a) every inline <script> block COMPILES (catches syntax errors from the edits)
//  (b) the 17-category restructure invariants hold (CATEGORIES/CAT_OF/SETTINGS_CONFIG)
// Run: node build_tools/_s732_verify.js
const fs = require('fs');
const path = 'G:/Project Folder/Master FM/src/customize.html';
const html = fs.readFileSync(path, 'utf8');
let fail = 0;
const must = (cond, msg) => { console.log((cond ? 'PASS  ' : 'FAIL  ') + msg); if (!cond) fail++; };

// (a) compile every script block
const scripts = [...html.matchAll(/<script\b[^>]*>([\s\S]*?)<\/script>/gi)].map(m => m[1]);
let parseOK = true, nonEmpty = 0;
scripts.forEach((s, i) => {
    if (!s.trim()) return;
    nonEmpty++;
    try { new Function(s); }
    catch (e) { parseOK = false; console.log(`  SCRIPT[${i}] SYNTAX ERROR: ${e.message}`); }
});
must(parseOK, `all ${nonEmpty} non-empty <script> blocks compile (no syntax error)`);

// helper: slice a balanced literal following `const NAME =`
function sliceConst(name, open, close) {
    const idx = html.indexOf('const ' + name + ' =');
    if (idx < 0) throw new Error('no const ' + name);
    const start = html.indexOf(open, idx);
    let depth = 0, i = start;
    for (; i < html.length; i++) {
        if (html[i] === open) depth++;
        else if (html[i] === close) { depth--; if (depth === 0) { i++; break; } }
    }
    return html.slice(start, i);
}
const CATEGORIES = eval(sliceConst('CATEGORIES', '[', ']'));
const CAT_OF = eval('(' + sliceConst('CAT_OF', '{', '}') + ')');

// SETTINGS_CONFIG control ids (the `id: 'c-...'` form is unique to SETTINGS_CONFIG;
// CAT_OF uses 'c-...': and FLAT_TO_NESTED_MAP uses 'c-...': too, so anchor on `id:`).
const ids = [...html.matchAll(/\bid:\s*'(c-[a-z0-9-]+)'/g)].map(m => m[1]);
const idSet = new Set(ids);

must(ids.length === 120, `SETTINGS_CONFIG has 120 controls (got ${ids.length})`);
must(idSet.size === 120, `SETTINGS_CONFIG ids all unique (got ${idSet.size})`);
must(CATEGORIES.length === 13, `CATEGORIES has 13 entries (got ${CATEGORIES.length})`);
must(Object.keys(CAT_OF).length === 120, `CAT_OF maps 120 ids (got ${Object.keys(CAT_OF).length})`);

const catKeys = new Set(CATEGORIES.map(c => c.key));
// every control id has a category
const missing = ids.filter(id => !CAT_OF[id]);
must(missing.length === 0, `every SETTINGS_CONFIG id has a CAT_OF entry (missing: ${missing.join(',') || 'none'})`);
// no CAT_OF key that isn't a real control
const orphan = Object.keys(CAT_OF).filter(id => !idSet.has(id));
must(orphan.length === 0, `no CAT_OF key without a control (orphans: ${orphan.join(',') || 'none'})`);
// every CAT_OF value is a real category
const badCat = Object.entries(CAT_OF).filter(([id, c]) => !catKeys.has(c));
must(badCat.length === 0, `every CAT_OF value is a real category (bad: ${badCat.map(x => x[0]).join(',') || 'none'})`);
// every category non-empty + print counts
const counts = {};
CATEGORIES.forEach(c => counts[c.key] = 0);
ids.forEach(id => { if (CAT_OF[id]) counts[CAT_OF[id]]++; });
const empty = CATEGORIES.filter(c => counts[c.key] === 0).map(c => c.key);
must(empty.length === 0, `every category has >=1 control (empty: ${empty.join(',') || 'none'})`);
const total = Object.values(counts).reduce((a, b) => a + b, 0);
must(total === 120, `category counts sum to 120 (got ${total})`);

console.log('\nper-category counts (V1 order):');
CATEGORIES.forEach(c => console.log(`  ${c.icon}  ${c.key.padEnd(16)} ${String(counts[c.key]).padStart(3)}  ${c.label}`));
console.log('\n' + (fail === 0 ? 'ALL CHECKS PASS' : fail + ' CHECK(S) FAILED'));
process.exit(fail === 0 ? 0 : 1);
