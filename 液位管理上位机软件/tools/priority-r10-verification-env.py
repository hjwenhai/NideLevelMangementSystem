from pathlib import Path
import re
root=Path(__file__).resolve().parents[1]
for name in ['control-api-check.cjs','control-ui-check.cjs','expiry-policy-api-check.cjs','scheduled-average-check.cjs','readonly-interface-check.cjs','backup-api-check.cjs','restore-e2e-check.cjs','trend-api-ui-check.cjs','data-jobs-api-ui-check.cjs','retention-guard-check.cjs','startup-readonly-check.cjs','fault-diagnostics-check.cjs']:
 p=root/'tests'/name;s=p.read_text(encoding='utf-8-sig')
 s=re.sub(r"out=path\.join\(root,'(output/[^']+)'\)",r"out=path.join(root,process.env.LEVEL_VERIFICATION_DIR||'\1')",s)
 s=s.replace("path.join(root,'output/engineering/LevelManager.Host.dll')","path.join(root,process.env.LEVEL_UI_BUILD_ROOT||'output/engineering','LevelManager.Host.dll')")
 p.write_text(s,encoding='utf-8')
p=root/'src/LevelManager.Infrastructure/TankConfigurationExcel.cs';s=p.read_text(encoding='utf-8')
s=s.replace('new Font(new FontSize{Val=11},new FontName{Val="微软雅黑"}),new Font(new Bold(),new Color{Rgb="FFFFFFFF"},new FontSize{Val=11},new FontName{Val="微软雅黑"})','new Font{FontSize=new FontSize{Val=11},FontName=new FontName{Val="微软雅黑"}},new Font{Bold=new Bold(),Color=new Color{Rgb="FFFFFFFF"},FontSize=new FontSize{Val=11},FontName=new FontName{Val="微软雅黑"}}')
p.write_text(s,encoding='utf-8')
print('verification outputs isolated; Excel font schema corrected')
