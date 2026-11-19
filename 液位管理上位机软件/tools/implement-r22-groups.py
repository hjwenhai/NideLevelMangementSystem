from pathlib import Path
r=Path(__file__).resolve().parents[1];p=r/'src/LevelManager.Infrastructure/TankConfigurationExcel.cs';s=p.read_text(encoding='utf-8-sig')
s=s.replace('return new(){["channelId"]','return new(){["$tankCodes"]=config.Tanks.Select(t=>(t.Id,t.Code)).ToArray(),["channelId"]')
old='private static (string Name,Field[] Fields)[] ForDelivery(bool average)=>Sheets.Select(s=>(s.Name,Fields:average?s.Fields:s.Fields.Where(f=>f.Path!="controls.wirelessMeasurementEnabled").ToArray())).ToArray();'
new='private static (string Name,Field[] Fields)[] ForDelivery(bool average,bool separateGroups=false)=>Sheets.Select(s=>(s.Name,Fields:s.Fields.Where(f=>(average||f.Path!="controls.wirelessMeasurementEnabled")&&(!separateGroups||f.Path!="$groups")).ToArray())).ToArray();'
assert old in s;s=s.replace(old,new)
a=s.index('public static byte[] Export(');b=s.index('public static TankConfigurationPreview Preview',a)
exp=s[a:b].replace('ForDelivery(includeAverage)','ForDelivery(includeAverage,true)').replace('TANK-CONFIG-2','TANK-CONFIG-3')
exp=exp.replace('var count=fields', 'var count=fields')
needle='            Sheet("选择列表",'
extra='''            Sheet("罐组关联",new[]{new object?[]{"罐号","所属罐组"}}.Concat(template?[]:config.Tanks.SelectMany(t=>{var groups=config.Groups.Where(g=>g.TankIds.Contains(t.Id)).ToArray();return groups.Length==0?new[]{new object?[]{t.Code,""}}:groups.Select(g=>new object?[]{t.Code,choices["$groups"].Single(c=>c.Id==g.Id).Label});})));
'''
assert needle in exp;exp=exp.replace(needle,extra+needle)
needle='                ws.Worksheet=worksheet;'
extra='''                if(name=="罐组关联"){
                    var validations=new DataValidations();
                    foreach(var (column,key) in new[]{("A","$tankCodes"),("B","$groups")})if(choices[key].Length>0)validations.Append(new DataValidation{Type=DataValidationValues.List,AllowBlank=column=="B",ShowInputMessage=true,PromptTitle="一行一个关联",Prompt=column=="A"?"选择已有罐号；新增罐号可直接填写，须与主配置页一致":"每行选择一个罐组。同罐可多行；仅保留罐号且罐组留空表示清除全部关联",ShowErrorMessage=true,ErrorStyle=column=="A"?DataValidationErrorStyleValues.Warning:DataValidationErrorStyleValues.Stop,ErrorTitle="请核对关联",Error=column=="A"?"新增罐号可以继续填写；应与主配置页一致":"请从下拉选择一个罐组，不在同一格拼接多个选项",SequenceOfReferences=new ListValue<StringValue>{InnerText=column+"2:"+column+"32769"},Formula1=new Formula1(choiceNames[key])});
                    if(validations.HasChildren)worksheet.Append(validations);
                }
'''
exp=exp.replace(needle,extra+needle)
exp=exp.replace('下拉可选择一个罐组；多个罐组选项使用英文分号分隔；留空取消该罐的所有罐组关联。','在“罐组关联”页，每行一个罐号、一个罐组；同罐可多行。导入按该罐所列全部行替换关联。清除全部关联：保留罐号，罐组留空且仅此一行。未列出的罐保留原关联；关联页罐号必须在主配置页列出。')
s=s[:a]+exp+s[b:]
s=s.replace('var count=fields?.Length??(name=="填写说明"?2:4);','var count=fields?.Length??(name is "填写说明" or "罐组关联"?2:4);')
s=s.replace('var activeSheets=ForDelivery(includeAverage);var choices=Choices(current,tables);','var activeSheets=ForDelivery(includeAverage);var choices=Choices(current,tables);')
s=s.replace('if(rows.Count>(Sheets.Any(s=>s.Name==reader.Name)?129:2048))','if(rows.Count>(reader.Name=="罐组关联"?32769:Sheets.Any(s=>s.Name==reader.Name)?129:2048))')
needle='        List<string> issues=[];string Text(object? value)=>Convert.ToString(value,CultureInfo.InvariantCulture)?.Trim()??"";'
extra='''
        var format=data.GetValueOrDefault("填写说明")?.FirstOrDefault(row=>Text(row.ElementAtOrDefault(0))=="模板格式")?.ElementAtOrDefault(1)?.ToString();
        var separateGroups=format=="TANK-CONFIG-3";activeSheets=ForDelivery(includeAverage,separateGroups);
        if(separateGroups&&(!data.TryGetValue("罐组关联",out var associationRows)||associationRows.Count==0||associationRows[0].Length!=2||Text(associationRows[0][0])!="罐号"||Text(associationRows[0][1])!="所属罐组"))issues.Add("罐组关联：缺少工作表或表头不符");
'''
assert needle in s;s=s.replace(needle,needle+extra)
s=s.replace('Text(row[1])=="TANK-CONFIG-2"','Text(row[1])=="TANK-CONFIG-2"||Text(row[1])=="TANK-CONFIG-3"').replace('不是本系统TANK-CONFIG-1模板','不是本系统支持的储罐配置模板')
needle='        var proposed=current with{Tanks='
extra='''        if(separateGroups){
            var associationRows=data["罐组关联"].Skip(1).ToArray();
            foreach(var row in associationRows)if(Text(row.ElementAtOrDefault(0))=="")issues.Add("罐组关联：有罐组但未填写罐号");
            foreach(var rows in associationRows.Where(row=>Text(row.ElementAtOrDefault(0))!="").GroupBy(row=>Text(row[0]),StringComparer.OrdinalIgnoreCase)){
                var code=rows.Key;var tank=result.Values.FirstOrDefault(t=>t.Code.Equals(code,StringComparison.OrdinalIgnoreCase));
                if(!seenCodes.Contains(code)||tank is null){issues.Add("罐组关联："+code+"未在罐体与通讯页列出");continue;}
                try{var texts=rows.Select(row=>Text(row.ElementAtOrDefault(1))).ToArray();if(texts.Any(t=>t=="")&&texts.Length!=1)throw new ArgumentException("空罐组行不能与其他关联行混用");var ids=texts.Where(t=>t!="").Select(t=>Reference(t,"$groups",choices)).ToArray();if(ids.Distinct().Count()!=ids.Length)throw new ArgumentException("重复关联同一罐组");groupMemberships[tank.Id]=ids.ToHashSet();}catch(ArgumentException error){issues.Add("罐组关联 / "+code+"："+error.Message);}
            }
        }
'''
assert needle in s;s=s.replace(needle,extra+needle)
p.write_text(s,encoding='utf-8')
p=r/'tests/LevelManager.Tests/TankConfigurationExcelTests.cs';s=p.read_text(encoding='utf-8-sig').replace('Assert.Equal(6,doc.WorkbookPart!.Workbook!.Sheets!.Count())','Assert.Equal(7,doc.WorkbookPart!.Workbook!.Sheets!.Count())');p.write_text(s,encoding='utf-8')
print('Format3 group association sheet added with legacy format1/2 compatibility')
