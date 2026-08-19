import json

# Load Splendor concepts
with open('games/splendor/concepts.json', 'r', encoding='utf-8') as f:
    data = json.load(f)

actions = data.get('actions', [])
for action in actions:
    aid = action['id']
    print(f'Processing action: {aid}')

    # Remove <declaration> if present
    if '<declaration>' in action:
        del action['<declaration>']
        print(f'  Removed <declaration>')

    # Transform <trigger> wrapper: extract condition/cost/content to action top-level
    if '<trigger>' in action:
        trigger = action['<trigger>']

        # Move condition/cost/content from trigger to action level
        if '<ontology::condition>' in trigger:
            action['<ontology::condition>'] = trigger['<ontology::condition>']
            print(f'  Moved <ontology::condition>: {trigger["<ontology::condition>"]}')
        if '<ontology::cost>' in trigger:
            action['<ontology::cost>'] = trigger['<ontology::cost>']
            print(f'  Moved <ontology::cost>: {trigger["<ontology::cost>"]}')
        if '<ontology::content>' in trigger:
            action['<ontology::content>'] = trigger['<ontology::content>']
            print(f'  Moved <ontology::content>')

        # Remove the trigger wrapper
        del action['<trigger>']
        print(f'  Removed <trigger> wrapper')

with open('games/splendor/concepts.json', 'w', encoding='utf-8') as f:
    json.dump(data, f, ensure_ascii=False, indent=2)

print('\nSplendor concepts updated successfully')
