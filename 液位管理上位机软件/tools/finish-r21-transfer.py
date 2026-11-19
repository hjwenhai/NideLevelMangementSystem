from pathlib import Path
r=Path(__file__).resolve().parents[1]
p=r/'web/src/CapacityTables.vue'
s=p.read_text(encoding='utf-8-sig')
old="decimalRows?:DecimalCapacityRow[]}>('/api/capacity-tables/import-preview?'+q,file);data.value="
new="decimalRows?:DecimalCapacityRow[];kind?:CapacityDocument['kind'];heightUnit?:CapacityDocument['heightUnit'];volumeUnit?:CapacityDocument['volumeUnit'];referenceDensityKgM3?:number}>('/api/capacity-tables/import-preview?'+q,file);if(!draft.value)return;if(result.kind&&result.kind!==draft.value.kind)throw Error('文件类型为'+kinds[result.kind]+'，请先选择对应的表类型再导入');if(result.heightUnit)draft.value.heightUnit=result.heightUnit;if(result.volumeUnit)draft.value.volumeUnit=result.volumeUnit;if(result.referenceDensityKgM3!=null&&draft.value.kind==='StaticPressure')draft.value.referenceDensityKgM3=result.referenceDensityKgM3;oldHeight=draft.value.heightUnit;oldVolume=draft.value.volumeUnit;data.value="
assert old in s;s=s.replace(old,new)
s=s.replace('模板使用mm、m³，请与上方单位保持一致。','模板使用mm、m³。本系统导出文件附有资料页，导入时自动采用其中明确记录的单位和静压力参考密度；其他文件请按原始资料选择。')
p.write_text(s,encoding='utf-8')
p=r/'deployment/docs/r21客户反馈修改说明.html';s=p.read_text(encoding='utf-8-sig').replace('</p>html>','</p></html>');s=s.replace('<h2>五列小数罐容表</h2>','<h2>导入导出核对</h2><p>储罐配置支持导出后重新导入，先预览差异再确认保存。复制储罐需要填写新罐号与新地址，默认停用，并清除罐容表关联，避免误用原罐资料。本系统罐容表导出文件附有“资料”页，重新导入时自动采用其中明确记录的单位、表类型及静压力参考密度，数值不另行换算；其他Excel文件仍需核对选择单位。表类型不一致时提示重新选择，不混用主表与修正表。</p><h2>五列小数罐容表</h2>')
p.write_text(s,encoding='utf-8')
