import os

def r(path, old, new):
    with open(path, 'r', encoding='utf-8') as f: c = f.read()
    c = c.replace(old, new)
    with open(path, 'w', encoding='utf-8') as f: f.write(c)

path = "src/LinuxWebTool.Infrastructure/Security/AdminCredentialService.cs"
old = """            // 自动生成密码的场景：文件里保留了明文，每次启动都回显，避免用户忘记密码后无处可查。
            if (json.TryGetValue("generatedPassword", out var generated))
            {
                _logger.LogWarning("当前管理员凭据（自动生成，文件 {File}）：用户名 {UserName}，密码 {Password}。可通过网页右上角 ⚙ 修改，或用环境变量 Admin__Password 覆盖",
                    _filePath, userName, generated);
            }"""
new = """            // 自动生成密码的场景：文件里保留了明文，每次启动都回显，避免用户忘记密码后无处可查。
            var generated = obj["generatedPassword"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(generated))
            {
                _logger.LogWarning("当前管理员凭据（自动生成，文件 {File}）：用户名 {UserName}，密码 {Password}。可通过网页右上角 ⚙ 修改，或用环境变量 Admin__Password 覆盖",
                    _filePath, userName, generated);
            }"""

r(path, old, new)
