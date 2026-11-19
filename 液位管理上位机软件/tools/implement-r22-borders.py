from pathlib import Path
r=Path(__file__).resolve().parents[1]
p=r/'src/LevelManager.Infrastructure/TankConfigurationExcel.cs';s=p.read_text(encoding='utf-8-sig').replace('styles.Stylesheet.Save();','WorkbookBorders.Apply(styles.Stylesheet);styles.Stylesheet.Save();');p.write_text(s,encoding='utf-8')
p=r/'src/LevelManager.Infrastructure/CapacityExcel.cs';s=p.read_text(encoding='utf-8-sig');s=s.replace('part.Workbook = new Workbook(); var sheets','part.Workbook = new Workbook(); var styles=part.AddNewPart<WorkbookStylesPart>();styles.Stylesheet=WorkbookBorders.CreateStyles();styles.Stylesheet.Save(); var sheets')
s=s.replace('new Cell { DataType = number ?', 'new Cell { StyleIndex=row==1?1u:0u, DataType = number ?');p.write_text(s,encoding='utf-8')
p=r/'web/public/sheetio.js';s=p.read_text(encoding='utf-8-sig');old='<border><left/><right/><top/><bottom/><diagonal/></border>';new='<border>'+''.join('<'+side+' style="thin"><color rgb="FFD9E1E6"/></'+side+'>' for side in ['left','right','top','bottom'])+'<diagonal/></border>';assert old in s;s=s.replace(old,new).replace('borderId="0" xfId="0"','borderId="0" applyBorder="1" xfId="0"');p.write_text(s,encoding='utf-8')
p=r/'src/LevelManager.Infrastructure/StreamingHistoryWorkbook.cs';s=p.read_text(encoding='utf-8-sig');old='x.WriteElementString("border",S,"");';new='x.WriteStartElement("border",S);foreach(var side in new[]{"left","right","top","bottom"}){x.WriteStartElement(side,S);Attribute(x,"style","thin");x.WriteStartElement("color",S);Attribute(x,"rgb","FFD9E1E6");x.WriteEndElement();x.WriteEndElement();}x.WriteElementString("diagonal",S,"");x.WriteEndElement();';assert old in s;s=s.replace(old,new).replace('Attribute(x,"xfId","0");if(i>=2)','Attribute(x,"xfId","0");Attribute(x,"applyBorder","1");if(i>=2)');p.write_text(s,encoding='utf-8')
p=r/'src/LevelManager.Host/Program.cs';s=p.read_text(encoding='utf-8-sig');at=s.index('app.UseDefaultFiles();')
middleware='''app.Use(async(context,next)=>{
 var name=context.Request.Path.Value;
 if(name is "/templates/capacity-main.xlsx" or "/templates/capacity-static-pressure.xlsx" or "/templates/capacity-decimal.xlsx"){
  var file=Path.Combine(app.Environment.WebRootPath??Path.Combine(app.Environment.ContentRootPath,"wwwroot"),"templates",Path.GetFileName(name));
  var bytes=WorkbookBorders.AddToTemplate(await File.ReadAllBytesAsync(file,context.RequestAborted));context.Response.ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";context.Response.Headers.ContentDisposition="attachment; filename=\\\""+Path.GetFileName(name)+"\\\"";context.Response.Headers.CacheControl="no-cache";await context.Response.Body.WriteAsync(bytes,context.RequestAborted);return;
 }
 await next(context);
});
'''
s=s[:at]+middleware+s[at:];s=s.replace('revision="20261107-r22",stage=','revision="20261107-r22",build="20261107-r22-u1",stage=');p.write_text(s,encoding='utf-8')
print('Light-gray borders applied to all export paths and served templates')
