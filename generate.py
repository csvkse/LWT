import os, re
out = []
out.append('    public static void Initialize(Microsoft.Data.Sqlite.SqliteConnection db)')
out.append('    {')
out.append('        using var cmd = db.CreateCommand();')
out.append('        cmd.CommandText = @"')
for file in os.listdir('src/LinuxWebTool.Infrastructure/Persistence/Entities'):
    if not file.endswith('.cs'): continue
    with open('src/LinuxWebTool.Infrastructure/Persistence/Entities/' + file, 'r', encoding='utf-8') as f:
        content = f.read()
    m = re.search(r'\[SugarTable\(\"([^\"]+)\"\)\]', content)
    table_name = m.group(1) if m else file.replace('.cs', '')
    out.append(f'CREATE TABLE IF NOT EXISTS {table_name} (')
    cols = []
    for m in re.finditer(r'public\s+([^\s]+)\s+([^\s]+)\s*\{\s*get', content):
        typ, name = m.group(1).replace('?', ''), m.group(2)
        sql_type = 'TEXT'
        if re.search(r'(int|long|bool)', typ): sql_type = 'INTEGER'
        elif re.search(r'(float|double|decimal)', typ): sql_type = 'REAL'
        pk = ' PRIMARY KEY' if name == 'Id' else ''
        cols.append(f'  {name} {sql_type}{pk}')
    out.append(',\n'.join(cols))
    out.append(');')
out.append('";')
out.append('        cmd.ExecuteNonQuery();')
out.append('    }')
with open('DbInitCode.cs', 'w', encoding='utf-8') as f: f.write('\n'.join(out))
