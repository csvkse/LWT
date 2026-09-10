import os
import re

routes_dir = "src/LinuxWebTool.WebHost/Routes"

for filename in os.listdir(routes_dir):
    if not filename.endswith("Controller.cs"):
        continue
        
    filepath = os.path.join(routes_dir, filename)
    with open(filepath, "r", encoding="utf-8") as f:
        content = f.read()
        
    # Replace IActionResult -> IResult
    content = content.replace("IActionResult", "IResult")
    
    # Replace ControllerBase inheritance
    content = re.sub(r':\s*ControllerBase', ': MinimalApi.ControllerBase', content)
    
    with open(filepath, "w", encoding="utf-8") as f:
        f.write(content)
        
print("Replaced all controllers.")
