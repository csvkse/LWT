import os, re

def process_file(path):
    with open(path, 'r', encoding='utf-8') as f:
        content = f.read()

    content = re.sub(r'new\s*\{\s*message\s*=\s*(.+?)\s*\}', r'new MessageResponse(\1)', content)
    content = re.sub(r'new\s*\{\s*message\s*\}', r'new MessageResponse(message)', content)
    content = re.sub(r'new\s*\{\s*([a-zA-Z0-9_]+\.Id)\s*\}', r'new IdResponse(\1)', content)
    
    with open(path, 'w', encoding='utf-8') as f:
        f.write(content)

for root, _, files in os.walk('src/LinuxWebTool.WebHost/Routes'):
    for file in files:
        if file.endswith('.cs'):
            process_file(os.path.join(root, file))
