import urllib.request
import json

url = 'http://localhost:5050/schedule'
data = {
    'instruction': 'Test scheduling',
    'apply_mode': 'append',
    'per_day': 2,
    'duty_rule': 'Test rule',
    'base_url': 'mock',
    'prompt_mode': 'Regular',
    'model': 'mock-model',
    'api_key': 'test-key'
}

print(f"Connecting to {url}...")
req = urllib.request.Request(
    url, 
    data=json.dumps(data).encode('utf-8'), 
    headers={'Content-Type': 'application/json'}
)

try:
    with urllib.request.urlopen(req) as response:
        print(f"Status: {response.status}")
        while True:
            line = response.readline()
            if not line:
                break
            decoded = line.decode('utf-8').strip()
            if decoded:
                print(f"Received: {decoded}")
except Exception as e:
    print(f"Error: {e}")
