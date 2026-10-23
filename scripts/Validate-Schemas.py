"""Developer / CI check; requires jsonschema, never used by the player app."""
import copy
import json
from pathlib import Path
from jsonschema import Draft202012Validator

root = Path(__file__).resolve().parent.parent
def load(path):
    return json.loads((root / path).read_text(encoding='utf-8-sig'))

schemas = {name: load(f'schemas/{name}.schema.json') for name in ('catalog','package','capture')}
for schema in schemas.values():
    Draft202012Validator.check_schema(schema)
Draft202012Validator(schemas['catalog']).validate(load('catalog/catalog.json'))
for path in (root / 'examples').glob('*.release.json'):
    Draft202012Validator(schemas['capture']).validate(json.loads(path.read_text(encoding='utf-8-sig')))

package = {
    'schemaVersion': 1, 'gameId': 'example', 'gameTitle': 'Example', 'version': '1.0.0',
    'variants': [{'id': 'standalone', 'label': 'Standalone', 'storefront': 'standalone',
                  'fingerprints': [{'path': 'game.dat', 'sha256': 'a'*64}],
                  'files': [{'path': 'game.dat', 'payload': 'payload/game.dat', 'sha256': 'b'*64, 'originalSha256': 'a'*64}]}]
}
validator = Draft202012Validator(schemas['package'])
validator.validate(package)
for kind in ('missing-original', 'traversal', 'foreign-field', 'steam-build-on-standalone'):
    bad = copy.deepcopy(package)
    if kind == 'missing-original': del bad['variants'][0]['files'][0]['originalSha256']
    if kind == 'traversal': bad['variants'][0]['files'][0]['path'] = '../escape'
    if kind == 'foreign-field': bad['runScript'] = 'malicious'
    if kind == 'steam-build-on-standalone': bad['variants'][0]['steamBuildIds'] = ['100']
    assert list(validator.iter_errors(bad)), f'Schema failed to reject {kind}'
print('Schemas, official catalog, capture examples and negative cases passed.')
