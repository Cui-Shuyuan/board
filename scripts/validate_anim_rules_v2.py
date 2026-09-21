#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Run the Splendor rule ledger over v2 animation source events.

The ledger itself is the same rule checker used before, but this adapter feeds
it v2 events independently from the v2 compiler.  It does not use v1 sampled
state as truth.
"""
from __future__ import annotations
import argparse, json, sys
from pathlib import Path

ROOT=Path(__file__).resolve().parent.parent
sys.path.insert(0,str(ROOT/'scripts'))
import validate_anim_rules as ledger  # noqa: E402

def load(p): return json.loads(Path(p).read_text(encoding='utf-8'))

def to_old_event(ev):
    op=ev.get('op')
    if op == 'camera':
        return None
    out={'action':op,'at':ev.get('at',0),'dur':ev.get('dur',0)}
    if ev.get('lead') is not None: out['lead']=ev['lead']
    if ev.get('easing'): out['easing']=ev['easing']
    what={}
    if ev.get('concept'): what['concept']=ev['concept']
    if ev.get('parts'): what['parts']=ev['parts']
    if what: out['what']=what
    for k in ('template','palette','zone','source','destination','quantity','count','to','order','slot','stagger'):
        if ev.get(k) is not None: out[k]=ev[k]
    if op in ('create','ensure'):
        out['destination']=ev.get('zone') or ev.get('destination')
    if op=='ensure': out['action']='create'
    if op=='show': out['action']='showbox'; out['on']=1 if ev.get('picture') else 0; out['picture']=ev.get('picture')
    if op=='stack':
        out.update({'destination':ev.get('destination'),'capacity':ev.get('capacity'),
                    'real_templates':ev.get('real_templates'),'pad_template':ev.get('pad_template'),
                    'to':ev.get('to')})
    if op in ('highlight','point','fade','scale','wait'):
        out['action']=op
    if op=='transfer' and isinstance(ev.get('source'),str):
        out['source']=[ev['source']]
    return out

def main():
    ap=argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--game',default='splendor'); ap.add_argument('--track',default='full')
    a=ap.parse_args()
    base=ROOT/'games'/a.game/'tutorial'/'anim'/'v2'
    track=load(base/f'{a.track}.anim.json')
    stages={}
    default_stage=None
    for t in track.get('trees') or []:
        p=base/(t['stage'] if t['stage'].endswith('.json') else t['stage']+'.json')
        if p.exists():
            stages[t['id']]=load(p)
            if t['id']==track.get('default_tree') or default_stage is None: default_stage=load(p)
    facts=load(ROOT/'games'/a.game/'card_facts.json')
    anim={'trees':[{'id':t['id'],'world':t.get('world') or t['id']} for t in (track.get('trees') or [])],
          'cues':[]}
    for c in track.get('cues') or []:
        anim['cues'].append({'cue':c['id'],'tree':c.get('tree') or 'main',
                             'events':[x for x in (to_old_event(e) for e in (c.get('events') or [])) if x]})
    rep=ledger.Report()
    ledger.run(anim, default_stage, stages, facts, rep)
    for w in rep.warnings: print('WARN',*w)
    for e in rep.errors: print('ERR ',*e)
    if rep.errors:
        print(f'FAIL v2 rule ledger: {len(rep.errors)} errors, {len(rep.warnings)} warnings',file=sys.stderr)
        return 1
    print(f'OK   v2 rule ledger: {len(anim["cues"])} cues passed, {len(rep.warnings)} warnings')
    return 0

if __name__=='__main__':
    sys.exit(main())
