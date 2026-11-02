"""Read/write Parkasaurus saves (gzip JSON) without changing anything that isn't touched:
numbers keep their exact original text. Used to make test copies of a park."""
import gzip, json, sys


class Num(str):
    """A JSON number kept as its original text."""


def load(path):
    raw = gzip.open(path, 'rb').read().decode('utf-8')
    return json.loads(raw, parse_float=Num, parse_int=Num), raw


def dumps(v):
    if isinstance(v, Num):
        return str(v)
    if isinstance(v, str):
        return json.dumps(v, ensure_ascii=False)
    if v is True:
        return 'true'
    if v is False:
        return 'false'
    if v is None:
        return 'null'
    if isinstance(v, list):
        return '[' + ','.join(dumps(x) for x in v) + ']'
    if isinstance(v, dict):
        return '{' + ','.join(json.dumps(k, ensure_ascii=False) + ':' + dumps(x) for k, x in v.items()) + '}'
    raise TypeError(type(v))


def save(data, path):
    with gzip.open(path, 'wb') as f:
        f.write(dumps(data).encode('utf-8'))


def remove_objects(data, keep):
    """Removes placed objects for which keep(obj) is False, plus their child entries."""
    gone = [o for o in data['m_placedObjects'] if not keep(o)]
    ids = {o['m_guid'] for o in gone}
    for o in gone:
        ids.update(o.get('m_children', []))
    for k, v in data.items():
        if isinstance(v, list) and v and isinstance(v[0], dict) and 'm_guid' in v[0]:
            data[k] = [e for e in v if e.get('m_guid') not in ids]
    return len(gone)


if __name__ == '__main__':
    data, raw = load(sys.argv[1])
    print('round-trip identical:', dumps(data) == raw)
