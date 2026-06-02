const fs=require('fs');
const html=fs.readFileSync('src/customize.html','utf8');
function bal(i){const s=html.indexOf('{',i);let d=0,j=s;for(;j<html.length;j++){const c=html[j];if(c==='{')d++;else if(c==='}'){d--;if(d===0){j++;break;}}}return html.slice(i,j);}
function fn(sig){return bal(html.indexOf(sig));}
function co(n){return bal(html.indexOf('const '+n+' ='))+';';}
const M=(new Function(co('FLAT_TO_NESTED_MAP')+'\n'+fn('function deepMerge(')+'\n'+fn('function flatToNested(')+'\n'+fn('function getNestedAtPath(')+'\n'+fn('function nestedToFlat(')+'\n; return {deepMerge,flatToNested,nestedToFlat};'))();
const live=JSON.parse(fs.readFileSync('_live_overlay_config.json','utf8').replace(/^FEFF/,''));
// simulate customize: loadConfig sets State.nested=live, State.config=nestedToFlat(live)
const nested=M.deepMerge({},live);
const flat=M.nestedToFlat(live);
const OLD=M.flatToNested(flat);                    // pre-7.32 send (flat-only)
const NEW=M.deepMerge(nested, M.flatToNested(flat)); // 7.32 send (full nested + flat)
// diff: keys/paths in NEW not equal in OLD
function walk(o,p,acc){ if(o&&typeof o==='object'&&!Array.isArray(o)){for(const k of Object.keys(o))walk(o[k],p?p+'.'+k:k,acc);} else {acc[p]=JSON.stringify(o);} return acc;}
const a=walk(OLD,'',{}), b=walk(NEW,'',{});
const added=Object.keys(b).filter(k=>!(k in a));
const changed=Object.keys(b).filter(k=>k in a && a[k]!==b[k]);
console.log('layout.enabled  OLD='+(OLD.layout&&OLD.layout.enabled)+'  NEW='+(NEW.layout&&NEW.layout.enabled));
console.log('NEW-only keys (added by 7.32 deepMerge): '+(added.length));
added.forEach(k=>console.log('  + '+k+' = '+b[k]));
console.log('CHANGED values: '+changed.length);
changed.forEach(k=>console.log('  ~ '+k+': '+a[k]+' -> '+b[k]));
// sizing-relevant scan
const sz=[...added,...changed].filter(k=>/scale|width|height|size|canvas|layout|overall|opacity/i.test(k));
console.log('\nsizing-relevant diffs: '+(sz.length?sz.join(', '):'NONE'));
