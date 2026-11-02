"""Dump the GameObject tree of the object carrying a given script: dump_ui.py <ScriptName> [maxdepth]
NAME=<GameObject name> picks a specific object; ALL=1 also searches prefabs in sharedassets."""
import UnityPy, sys, glob, os, struct
script_name = sys.argv[1]; maxdepth = int(sys.argv[2]) if len(sys.argv) > 2 else 6
D = os.path.expanduser('~/.local/share/Steam/steamapps/common/Parkasaurus/Parkasaurus_Data')
extra = sorted(glob.glob(D + '/sharedassets*.assets')) + [D + '/resources.assets'] if os.environ.get('ALL') else []
env = UnityPy.load(*sorted(glob.glob(D + '/level?')), *extra, D + '/globalgamemanagers.assets', D + '/globalgamemanagers')
scripts = {}  # (file name, pathID) -> class name
for obj in env.objects:
    if obj.type.name == 'MonoScript':
        scripts[(obj.assets_file.name, obj.path_id)] = obj.read().m_ClassName

def script_of(obj):
    """Script class name of a MonoBehaviour, parsed from its raw data (no type tree needed)."""
    raw = obj.get_raw_data()
    file_id, path_id = struct.unpack_from('<iq', raw, 16)
    af = obj.assets_file
    name = af.name if file_id == 0 else os.path.basename(af.externals[file_id - 1].path)
    return scripts.get((name, path_id), f'?{name}:{path_id}')

def comps(go):
    out = []
    for c in go.m_Component:
        ptr = c.component
        t = ptr.type.name if ptr.type else '?'
        if t == 'MonoBehaviour':
            out.append(script_of(ptr.deref()))
        elif t not in ('RectTransform', 'Transform', 'CanvasRenderer'):
            out.append(t)
    return out

def walk(tr, depth, prefix=''):
    go = tr.m_GameObject.read()
    info = ''
    if hasattr(tr, 'm_SizeDelta'):
        p, s = tr.m_AnchoredPosition, tr.m_SizeDelta
        info = f' pos=({p.x:.0f},{p.y:.0f}) size=({s.x:.0f},{s.y:.0f})'
    print(f"{prefix}{go.m_Name}{'' if go.m_IsActive else ' [inactive]'}{info}  {comps(go)}")
    if depth >= maxdepth and not os.environ.get("FULL"):
        if len(tr.m_Children): print(prefix + f'  ... {len(tr.m_Children)} children')
        return
    for ch in tr.m_Children:
        walk(ch.read(), depth + 1, prefix + '  ')

for obj in env.objects:
    if obj.type.name != 'MonoBehaviour' or script_of(obj) != script_name:
        continue
    raw = obj.get_raw_data()
    _, go_path = struct.unpack_from('<iq', raw, 0)
    go = obj.assets_file.objects[go_path].read()
    if os.environ.get('NAME') and go.m_Name != os.environ['NAME']:
        continue
    print('=== found in', obj.assets_file.name, '->', go.m_Name)
    walk(go.m_Component[0].component.read(), 0)
    break
