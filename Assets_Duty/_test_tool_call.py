import json, urllib.request, urllib.error

url = 'http://localhost:11434/v1/chat/completions'
TOOL_DEFINITION = [{
    'type': 'function',
    'function': {
        'name': 'fill_schedule',
        'description': 'Submit INI-formatted schedule.',
        'parameters': {
            'type': 'object',
            'properties': {'schedule': {'type': 'string', 'description': 'Complete INI text.'}},
            'required': ['schedule'],
        },
    }
}]

for model in ['Qwen-3.5-9B-uncensored:latest', 'qwen3.5:9b']:
    for think in [False, True]:
        payload = {
            'model': model,
            'messages': [{'role': 'user', 'content': 'Use the fill_schedule tool to return: schedule=TEST'}],
            'tools': TOOL_DEFINITION,
            'tool_choice': {'type': 'function', 'function': {'name': 'fill_schedule'}},
            'think': think,
            'stream': False,
        }
        data = json.dumps(payload).encode('utf-8')
        req = urllib.request.Request(url=url, data=data, method='POST', headers={'Content-Type': 'application/json'})
        try:
            with urllib.request.urlopen(req, timeout=60) as resp:
                result = json.loads(resp.read().decode('utf-8'))
                msg = result.get('choices', [{}])[0].get('message', {})
                tc = msg.get('tool_calls', [])
                content = msg.get('content', '')
                print(f'{model} think={think}: content={repr(content[:100])} tool_calls={len(tc)}')
                if tc:
                    for t in tc:
                        fn = t.get('function', {})
                        print(f'  -> {fn.get("name")}: {repr(str(fn.get("arguments",""))[:100])}')
        except Exception as ex:
            print(f'{model} think={think}: ERROR {ex}')
