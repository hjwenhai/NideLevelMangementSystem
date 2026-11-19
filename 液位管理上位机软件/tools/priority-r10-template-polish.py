from pathlib import Path
root=Path(__file__).resolve().parents[1]
p=root/'src/LevelManager.Infrastructure/TankConfigurationExcel.cs';s=p.read_text(encoding='utf-8')
s=s.replace('new Columns(new Column{Min=1,Max=1,Width=name=="罐体与通讯"?42:24,CustomWidth=true},new Column{Min=2,Max=80,Width=name=="填写说明"?110:24,CustomWidth=true})','ColumnsFor(name,ForDelivery(includeAverage).FirstOrDefault(s=>s.Name==name).Fields)')
s=s.replace('Height=at==1?42:name=="填写说明"?66:22','Height=at==1?42:name=="填写说明"?Math.Max(22,Math.Ceiling(values.Select(v=>Convert.ToString(v,CultureInfo.InvariantCulture)?.Length??0).Max()/55d)*18+6):22')
s=s.replace('data.Append(row);}ws.Worksheet=worksheet;','data.Append(row);}\n                var definitions=ForDelivery(includeAverage).FirstOrDefault(s=>s.Name==name).Fields;\n                if(definitions is not null){var validations=new DataValidations();for(var i=0;i<definitions.Length;i++){var kind=definitions[i].Kind;var values=kind switch{"bool"=>"是,否","shape"=>"Vertical,Sphere,FloatingRoof","temperature"=>includeAverage?"None,Ats,Wireless":"None,Ats,Instrument","pressureUnit"=>"KPa,MPa,Bar",_=>null};if(values is not null)validations.Append(new DataValidation{Type=DataValidationValues.List,AllowBlank=false,ShowErrorMessage=true,ErrorTitle="请选择模板选项",Error="请使用下拉列表中的值",SequenceOfReferences=new ListValue<StringValue>{InnerText=ColumnName(i+1)+"2:"+ColumnName(i+1)+"129"},Formula1=new Formula1("\\\""+values+"\\\"")});}if(validations.HasChildren)worksheet.Append(validations);}\n                ws.Worksheet=worksheet;')
s=s.replace('if(text.Length>2000)','if(text.Length>(field.Path=="$groups"?20000:2000))')
insert='''    private static Columns ColumnsFor(string name,Field[]? fields)
    {
        var count=fields?.Length??(name=="填写说明"?2:4);var columns=new Columns();
        for(var i=0;i<count;i++)
        {
            var field=fields?.ElementAt(i);double width;
            if(name=="填写说明")width=i==1?110:24;
            else if(name=="参考编号")width=i==1?44:i>=2?36:18;
            else width=field?.Path=="id"||field?.Kind.StartsWith("guid",StringComparison.Ordinal)==true?44:field?.Path=="$groups"?72:field?.Path=="notes"?55:field?.Kind=="bool"?18:24;
            columns.Append(new Column{Min=(uint)i+1,Max=(uint)i+1,Width=width,CustomWidth=true});
        }return columns;
    }
'''
assert '    private static Field[] BuildMeter()' in s;s=s.replace('    private static Field[] BuildMeter()',insert+'    private static Field[] BuildMeter()')
p.write_text(s,encoding='utf-8')
# Add a real due-time check for permission revision invalidation independently of password changes.
p=root/'tests/scheduled-average-check.cjs';s=p.read_text(encoding='utf-8')
needle=' const second=await until'
assert needle in s
addition=''' cfg=(await call('/api/config')).value;const permissionScheduleId=crypto.randomUUID();cfg.schedules.push({...cfg.schedules[0],id:permissionScheduleId,name:'权限矩阵变化重新授权验证',time:localTime(tomorrowDue),authorizedById:null,authorizedSecurityVersion:0,automaticOperationConfirmed:true});saved=await call('/api/config','PUT',cfg);assert.equal(saved.status,200);cfg=saved.value;let policy=(await call('/api/permissions')).value.matrix;policy.roles.find(r=>r.role==='Viewer').permissions=[];let policyResult=await call('/api/permissions','PUT',policy);assert.equal(policyResult.status,200);policy=policyResult.value;policy.roles.find(r=>r.role==='Viewer').permissions=['HistoryRead','DataExport'];assert.equal((await call('/api/permissions','PUT',policy)).status,200);
 const permissionFailure=await until(async()=>{const rows=(await call('/api/data-jobs')).value;return rows.find(j=>j.kind==='AverageTemperatureDispatch'&&j.requestJson?.includes(permissionScheduleId)&&j.state==='Failed')},750);assert(permissionFailure.error.includes('权限'));assert.equal((await call('/api/wireless-tasks')).value.length,0);
'''
s=s.replace(needle,addition+needle).replace("'no native tasks created and no port opened'","'permission matrix revision invalidates old unattended authority even when functional grants are restored','no native tasks created and no port opened'").replace('PASS: 7 scheduled-average','PASS: 8 scheduled-average')
p.write_text(s,encoding='utf-8')
print('template identifiers widened, dropdowns and due-time permission regression added')
