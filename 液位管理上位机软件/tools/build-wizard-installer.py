from pathlib import Path
import argparse
import hashlib
import json
import re
import subprocess
import sys

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
WIZARD = ROOT / 'deployment/wizard'
PAYLOAD = ROOT / 'output/r24-feedback_20261118/payload'
OUTPUT = ROOT / 'output/客户安装向导_r24_20261118'
QA = ROOT / 'output/r24-feedback_20261118/qa/installer'
COMPILER = ROOT / '.build-tools/innosetup64/ISCC.exe'

def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()

def branding():
    image = Image.new('RGB', (328, 628), '#163442')
    draw = ImageDraw.Draw(image)
    font = lambda size: ImageFont.truetype('C:/Windows/Fonts/msyh.ttc', size)
    icon = Image.open(ROOT / 'assets/level.png').convert('RGBA')
    icon.thumbnail((130, 130))
    image.paste(icon, (40, 65), icon)
    for y, text, size, color in [(240, '液位管理', 34, 'white'), (292, '系统', 34, 'white'), (390, '耐德东京计装', 23, '#a9d3d2'), (550, 'r24', 22, '#a9d3d2')]:
        draw.text((32, y), text, font=font(size), fill=color)
    image.save(WIZARD / 'wizard-large.bmp')
    icon = Image.open(ROOT / 'assets/level.png').convert('RGBA')
    icon.thumbnail((72, 72))
    small = Image.new('RGB', (110, 110), 'white')
    small.paste(icon, ((110-icon.width)//2, (110-icon.height)//2), icon)
    small.save(WIZARD / 'wizard-small.bmp')

def language():
    replacements = {
        'LanguageName': '简体中文', 'LanguageID': '$0804',
        'SetupAppTitle': '安装', 'SetupWindowTitle': '%1 安装向导', 'UninstallAppTitle': '卸载', 'UninstallAppFullTitle': '卸载 %1',
        'InformationTitle': '提示', 'ConfirmTitle': '请确认', 'ErrorTitle': '错误',
        'SetupFileCorrupt': '安装文件校验失败，请重新获取安装程序。',
        'SetupFileCorruptOrWrongVer': '安装文件损坏或版本不兼容，请重新获取安装程序。',
        'AdminPrivilegesRequired': '安装需要管理员权限，请允许 Windows 授权后继续。',
        'SetupAlreadyRunning': '安装向导已经运行。', 'WindowsVersionNotSupported': '本程序支持 Windows 10 或 Windows 11 64 位系统。',
        'ExitSetupTitle': '退出安装', 'ExitSetupMessage': '安装尚未完成。是否退出安装向导？',
        'ButtonBack': '< 上一步(&B)', 'ButtonNext': '下一步(&N) >', 'ButtonInstall': '安装(&I)', 'ButtonOK': '确定',
        'ButtonCancel': '取消', 'ButtonYes': '是(&Y)', 'ButtonNo': '否(&N)', 'ButtonYesToAll': '全部是', 'ButtonNoToAll': '全部否',
        'ButtonFinish': '完成(&F)', 'ButtonBrowse': '浏览(&B)…', 'ButtonWizardBrowse': '浏览(&B)…', 'ButtonNewFolder': '新建文件夹',
        'SelectLanguageTitle': '选择安装语言', 'ClickNext': '点击“下一步”继续，或点击“取消”退出。',
        'BrowseDialogTitle': '选择文件夹', 'BrowseDialogLabel': '选择文件夹后点击“确定”。', 'NewFolderName': '新建文件夹',
        'WelcomeLabel1': '欢迎使用 [name] 安装向导', 'WelcomeLabel2': '本向导将安装 [name/ver]。',
        'WizardSelectDir': '选择程序安装位置', 'SelectDirDesc': '将 [name] 安装到哪个文件夹',
        'SelectDirLabel3': '程序文件将安装到以下目录。业务数据位置将在下一步设置。',
        'SelectDirBrowseLabel': '点击“下一步”继续，或点击“浏览”选择其他位置。',
        'DiskSpaceGBLabel': '程序安装至少需要 [gb] GB 可用空间。', 'DiskSpaceMBLabel': '程序安装至少需要 [mb] MB 可用空间。',
        'CannotInstallToNetworkDrive': '程序请选择本机磁盘目录。', 'CannotInstallToUNCPath': '程序请选择本机磁盘目录。',
        'InvalidPath': '请输入带盘符的完整路径，例如 D:\\LevelManager。', 'InvalidDrive': '所选磁盘不存在或不可访问。',
        'DiskSpaceWarningTitle': '磁盘空间不足', 'DiskSpaceWarning': '所选磁盘可用空间不足。请返回选择其他位置。是否仍要继续？',
        'DirNameTooLong': '目录路径过长，请选择更短的路径。', 'InvalidDirName': '目录名称无效。',
        'DirExistsTitle': '目录已经存在', 'DirExists': '目录 %1 已经存在，是否继续使用？',
        'DirDoesntExistTitle': '创建程序目录', 'DirDoesntExist': '目录 %1 不存在。是否创建？',
        'WizardSelectTasks': '设置快捷方式', 'SelectTasksDesc': '选择要创建的快捷方式',
        'SelectTasksLabel2': '选择所需项目后点击“下一步”。',
        'WizardReady': '准备安装', 'ReadyLabel1': '已准备好安装 [name]。',
        'ReadyLabel2a': '核对下列设置，点击“安装”开始。需要调整时点击“上一步”。',
        'ReadyLabel2b': '点击“安装”开始。', 'ReadyMemoTasks': '快捷方式',
        'WizardPreparing': '安装前检查', 'PreparingDesc': '正在检查安装条件。',
        'CannotContinue': '暂时无法继续，请根据提示处理后重试。',
        'WizardInstalling': '正在安装', 'InstallingLabel': '正在安装 [name]，请稍候。',
        'FinishedHeadingLabel': '[name] 安装完成', 'FinishedLabel': '[name] 已安装完成，可从桌面或开始菜单打开。',
        'FinishedLabelNoIcons': '[name] 已安装完成。', 'ClickFinish': '点击“完成”退出安装向导。',
        'FinishedRestartLabel': 'Windows 运行环境需要重启后生效，请保存工作后选择重启时间。',
        'FinishedRestartMessage': '运行环境需要重启。是否现在重启电脑？', 'YesRadio': '现在重启', 'NoRadio': '稍后自行重启',
        'SetupAborted': '安装未完成，请保留日志并按提示处理后重试。',
        'AbortRetryIgnoreRetry': '重试', 'AbortRetryIgnoreIgnore': '忽略', 'AbortRetryIgnoreCancel': '取消安装',
        'RetryCancelRetry': '重试', 'RetryCancelCancel': '取消',
        'StatusCreateDirs': '正在创建程序目录…', 'StatusExtractFiles': '正在安装程序文件…',
        'StatusCreateIcons': '正在创建快捷方式…', 'StatusCreateRegistryEntries': '正在注册程序信息…',
        'StatusSavingUninstall': '正在保存卸载信息…', 'StatusRunProgram': '正在完成安装…', 'StatusRollback': '正在回退安装文件…',
        'UninstallNotFound': '找不到卸载记录。', 'ConfirmUninstall': '是否卸载 %1？业务数据及授权将保留。',
        'UninstallStatusLabel': '正在卸载 %1，请稍候。', 'UninstalledAll': '%1 已卸载。业务数据与授权已保留。',
        'UninstalledMost': '%1 的程序文件已移除，部分在用文件或业务数据已保留。',
        'UninstallDataCorrupted': '卸载记录损坏，请联系供方处理。',
        'AboutSetupMenuItem': '关于安装程序', 'AboutSetupTitle': '关于安装程序',
    }
    template = (ROOT / '.build-tools/innosetup64/Default.isl').read_text(encoding='utf-8-sig')
    unknown = set(replacements) - set(re.findall(r'^([A-Za-z0-9]+)=', template, re.M))
    for key in unknown:
        replacements.pop(key)
    lines = []
    for line in template.splitlines():
        key = line.partition('=')[0]
        lines.append(key+'='+replacements[key] if key in replacements else line)
    text = '\n'.join(lines).replace('LanguageCodePage=0','LanguageCodePage=936')
    text = text.replace(';DialogFontName=', 'DialogFontName=Microsoft YaHei')
    text = re.sub(r';DialogFontSize=\d+', 'DialogFontSize=10', text)
    text = re.sub(r';WelcomeFontName=.*', 'WelcomeFontName=Microsoft YaHei', text)
    (WIZARD / 'ChineseSimplified.isl').write_text(text, encoding='utf-8-sig')

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--payload-dir', type=Path, default=PAYLOAD)
    payload = parser.parse_args().payload_dir.resolve()
    OUTPUT.mkdir(parents=True, exist_ok=True)
    QA.mkdir(parents=True, exist_ok=True)
    manifest = json.loads((payload/'package-manifest.json').read_text(encoding='utf-8-sig'))
    assert manifest['build'] == '20261118-r24'
    for item in manifest['files']:
        path = (payload/item['path']).resolve()
        assert path.is_relative_to(payload), item['path']
        assert digest(path) == item['sha256'], item['path']
    branding()
    language()
    result = subprocess.run([str(COMPILER), '/DPayloadRoot='+str(payload), '/DOutputRoot='+str(OUTPUT), str(WIZARD/'LevelManager.iss')], stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    log = result.stdout.decode('utf-8', errors='replace')
    (QA/'release-build.log').write_text(log, encoding='utf-8')
    print(log[-6000:])
    if result.returncode:
        sys.exit(result.returncode)
    exe = OUTPUT/'液位管理系统_r24_安装程序_20261118.exe'
    (OUTPUT/(exe.name+'.sha256')).write_text(digest(exe)+'  '+exe.name+'\n', encoding='utf-8')
    print(json.dumps({'path':str(exe),'bytes':exe.stat().st_size,'sha256':digest(exe)},ensure_ascii=False))

if __name__ == '__main__':
    main()
