import os

def r(path, old, new):
    with open(path, 'r', encoding='utf-8') as f: c = f.read()
    c = c.replace(old, new)
    with open(path, 'w', encoding='utf-8') as f: f.write(c)

path = "src/LinuxWebTool.Infrastructure/Persistence/TranscodePresetSeeder.cs"
old = """public static class TranscodePresetSeeder
{
    public static void Seed(DbConnectionFactory factory)"""
new = """public static partial class TranscodePresetSeeder
{
    [DapperAot]
    public static void Seed(DbConnectionFactory factory)"""

r(path, old, new)
