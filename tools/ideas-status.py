#!/usr/bin/env python3
"""Preview reviewed discussion statuses; --apply writes only the tracked status assignments."""
import argparse
import json
import subprocess
from pathlib import Path

REPO = 'mattxslv/Selene'


def gh(*args, data=None):
    return subprocess.check_output(['gh', *args], input=json.dumps(data) if data is not None else None, text=True)


def graphql(query, **variables):
    data = json.loads(gh('api', 'graphql', '--input', '-', data={'query': query, 'variables': variables}))
    if data.get('errors'):
        raise RuntimeError(data['errors'])
    return data['data']


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    config = json.loads((Path(__file__).parents[1] / '.github/idea-status.json').read_text())
    if any(label not in config['labels'] for label in config['discussions'].values()):
        parser.error('Unknown status in assignments')
    # Fetch every configured discussion before making changes; no unreviewed idea is labelled.
    rows = []
    query = 'query($n:Int!){repository(owner:"mattxslv",name:"Selene"){discussion(number:$n){id title category{name} labels(first:100){nodes{id name}}}}}'
    for number, label in config['discussions'].items():
        row = graphql(query, n=int(number))['repository']['discussion']
        if row is None or row['category']['name'] != 'Ideas':
            parser.error(f'#{number} is not an Ideas discussion')
        rows.append((number, label, row))
        print(f'#{number}: {label} - {row["title"]}')
    if not args.apply:
        print('Preview only. Use --apply to update these statuses.')
        return
    for name, attributes in config['labels'].items():
        gh('label', 'create', name, '--repo', REPO, '--color', attributes['color'], '--description', attributes['description'], '--force')
    labels = graphql('{repository(owner:"mattxslv",name:"Selene"){labels(first:100){nodes{id name}}}}')['repository']['labels']['nodes']
    ids = {label['name']: label['id'] for label in labels}
    for number, label, row in rows:
        current = row['labels']['nodes']
        # Add the new status first, so a failed API call cannot silently lose the old status.
        if not any(item['name'] == label for item in current):
            graphql('mutation($id:ID!,$labels:[ID!]!){addLabelsToLabelable(input:{labelableId:$id,labelIds:$labels}){clientMutationId}}', id=row['id'], labels=[ids[label]])
        old = [item['id'] for item in current if item['name'] in config['labels'] and item['name'] != label]
        if old:
            graphql('mutation($id:ID!,$labels:[ID!]!){removeLabelsFromLabelable(input:{labelableId:$id,labelIds:$labels}){clientMutationId}}', id=row['id'], labels=old)
    print('Reviewed discussion statuses applied. No comments posted or discussions closed.')


if __name__ == '__main__':
    main()
