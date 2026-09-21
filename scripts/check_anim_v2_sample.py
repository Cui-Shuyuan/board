#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Reconcile Unity v2 samples with hand-written v2 exit contracts.

Sample is produced by BoardGameTutorial.Editor.TutorialV2Sampler.DumpStateV2.
"""
from __future__ import annotations
import argparse, json, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / 'scripts'))
import anim_schema_v2 as schema  # noqa: E402

def load(p): return json.loads(Path(p).read_text(encoding='utf-8'))

def contract_zones(part):
    zones=(part or {}).get('zones') or {}
    if isinstance(zones,dict): return zones
    out={}
    for e in zones:
        if isinstance(e,dict): out[e.get('zone')]=e
    return out

FACE={'up':2,'down':1,'hidden':0,'face_up':2,'face_down':1}

def face_num(v):
    if v is None or v=='' : return None
    return FACE.get(v)

def main():
    ap=argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--game',default='splendor')
    ap.add_argument('--track',default='full')
    ap.add_argument('--source')
    ap.add_argument('--sample')
    a=ap.parse_args()
    src=Path(a.source) if a.source else ROOT/'games'/a.game/'tutorial'/'anim'/'v2'/f'{a.track}.anim.json'
    smp=Path(a.sample) if a.sample else src.with_name(f'{a.track}.v2sample.json')
    if not src.exists() or not smp.exists():
        print(f'missing source or sample: {src} / {smp}',file=sys.stderr); return 2
    track=schema.resolve_track(load(src)); sdoc=load(smp)
    compiled_path=src.with_name(src.name.replace('.anim.json','.compiled.json'))
    compiled=load(compiled_path) if compiled_path.exists() else {'cues':[]}
    end_by={c['id']:c for c in compiled.get('cues') or []}
    sample={c['cue']:c for c in sdoc.get('cues') or []}
    errors=[]
    for cue in track.get('cues') or []:
        cid=cue.get('id'); sc=sample.get(cid)
        if sc is None:
            errors.append(f'{cid}: missing sample'); continue
        # exit contract
        want_zones=contract_zones((cue.get('script') or {}).get('exit') or {})
        got=sc.get('items') or []
        # picture
        want_pic=((cue.get('script') or {}).get('exit') or {}).get('picture')
        got_pic=sc.get('picture')
        if (want_pic or None)!=(got_pic or None):
            errors.append(f'{cid} picture: want {want_pic!r} got {got_pic!r}')
        # Stronger reconciliation: the sampled logical order/zone/face must
        # equal the compiled end_state, item by item.  This is what catches the
        # v2 order-drift class inside a cue.
        end_cue=end_by.get(cid)
        if end_cue is not None:
            want_items={x['Id']:(x['ZoneId'],x['Order'],x['Face']) for x in end_cue.get('end_state',{}).get('components') or []}
            got_items={it.get('id'):(it.get('zone'),it.get('order'),
                                     {'up':2,'face_up':2,'down':1,'face_down':1,'hidden':0}.get(it.get('face')))
                       for it in got if it.get('visible')}
            for iid in sorted(set(want_items)|set(got_items)):
                if want_items.get(iid)!=got_items.get(iid):
                    errors.append(f'{cid} state-sync {iid}: want {want_items.get(iid)} got {got_items.get(iid)}')

        for zone,spec in want_zones.items():
            spec=spec or {}
            items=got
            zi=[it for it in items if it.get('zone')==zone]
            if 'count' in spec and 'items' not in spec:
                if len(zi)!=int(spec['count']):
                    errors.append(f'{cid} {zone}.count: want {spec["count"]} got {len(zi)}')
            for i,w in enumerate(spec.get('items') or []):
                matched=[it for it in zi if (not w.get('template') or it.get('kind','').split('|')[0]==w['template'])
                         and (not w.get('palette') or it.get('kind','').split('|')[-1]==w['palette'])]
                wc=int(w.get('count',1))
                if len(matched)!=wc:
                    errors.append(f'{cid} {zone}.items[{i}]: want {wc} got {len(matched)}')
                    continue
                fn=face_num(w.get('face'))
                if fn is not None:
                    bad=[it for it in matched if it.get('face')!={'2':'up','1':'down','0':'hidden'}.get(str(fn))]
                    if bad:
                        errors.append(f'{cid} {zone}.items[{i}].face: want {w.get("face")} got {len(bad)} mismatches')
    for e in errors: print('ERR  '+e)
    if errors:
        print(f'FAIL {src.name}: {len(errors)} sample mismatches',file=sys.stderr); return 1
    print(f'OK   {src.name}: {len(sample)} sampled cues match exit contracts')
    return 0

if __name__=='__main__':
    sys.exit(main())
