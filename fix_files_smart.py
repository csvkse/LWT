import os

def fix_file(path):
    if not os.path.exists(path): return
    with open(path, 'r', encoding='utf-8') as f:
        content = f.read()

    # Manual brace counting for `new { message = `
    while True:
        idx = content.find('new { message = ')
        if idx == -1:
            idx = content.find('new { message }')
            if idx == -1:
                break
            else:
                start = idx
                brace_start = content.find('{', start)
                brace_end = content.find('}', brace_start)
                content = content[:start] + 'new MessageResponse(message)' + content[brace_end+1:]
                continue
                
        start = idx
        brace_start = content.find('{', start)
        
        # count braces
        brace_count = 0
        brace_end = -1
        for i in range(brace_start, len(content)):
            if content[i] == '{': brace_count += 1
            elif content[i] == '}':
                brace_count -= 1
                if brace_count == 0:
                    brace_end = i
                    break
        
        inner = content[brace_start+1:brace_end].strip()
        # inner is `message = "..."`
        # we want to extract `"..."`
        val_start = inner.find('=') + 1
        val = inner[val_start:].strip()
        
        if 'tooLarge' in inner or 'binary' in inner or 'needRecursive' in inner:
            # Special cases, just manually replace them later, break for now
            break
            
        new_str = f"new MessageResponse({val})"
        content = content[:start] + new_str + content[brace_end+1:]

    with open(path, 'w', encoding='utf-8') as f:
        f.write(content)

for root, _, files in os.walk('src/LinuxWebTool.WebHost/Routes'):
    for file in files:
        if file.endswith('.cs'):
            fix_file(os.path.join(root, file))
