export interface TrendAxisSetting {mode:'Auto'|'Fixed';minimumSpan:number;minimum:number|null;maximum:number|null}
export interface Channel {id:string;name:string;protocol:'DirectRtu'|'IfxL25'|'GatewayRtu'|'GatewayTcp';portName:string;baudRate:number;dataBits:number;parity:'None'|'Even'|'Odd';stopBits:number;host:string;tcpPort:number;unitId:number;pollIntervalMs:number;readTimeoutMs:number;readRetries:number;enabled:boolean;isGateway?:boolean;physicalKey?:string}
export interface Tank {id:string;code:string;name:string;productNumber:string;batch:string;diameterMm:number|null;nominalCapacityM3:number|null;notes:string;instrumentTag:string;medium:string;mediumId:string|null;pinnedAtUtc:string|null;atsHeightsMm:(number|null)[];pressure1:PressureCalibration;pressure2:PressureCalibration;zone:string;channelId:string;address:number;shape:'Vertical'|'Sphere'|'FloatingRoof';heightMm:number;temperatureMode:'None'|'Ats'|'Wireless';atsPointCount:number;enabled:boolean;hidden:boolean;metering:MeteringSettings;controls:ControlSettings;alarms:AlarmSettings;trends:{quantities:TrendQuantity[];colors:string[];axes?:Partial<Record<TrendQuantity,TrendAxisSetting>>}}
export interface Configuration {startReadOnlyAcquisitionAtStartup:boolean;revision:number;channels:Channel[];tanks:Tank[];display:DisplaySettings;groups:TankGroup[];schedules:SnapshotSchedule[];media:MediumDefinition[];historyIntervalSeconds:number;retentionMonths:number;readingExpirySeconds:number}
import {ref} from 'vue';
export const currentPermissions=ref<string[]>([]);
export const productModules=ref<string[]>([]);
export const optionalOperations:Record<string,string>={MeasureII:'operation-interface',MeasureIII:'operation-bottom',Lower:'operation-lower',Density:'operation-density',RemoteLevel:'operation-level-adjust'};
export const operationAvailable=(command:string)=>!optionalOperations[command]||productModules.value.includes(optionalOperations[command]);
export const basicOperations:InstrumentCommand[]=['MeasureI','Stop','Hoist','Repeatability','OscillationAvoidance'];
export const ifxTableAddress=(slot:number,unitId:number)=>32*(unitId-1)+slot-1;
export const ifxInternalSlot=(address:number,unitId:number)=>address-32*(unitId-1)+1;
export const can=(permission:string)=>currentPermissions.value.includes(permission);
export interface Session {needsSetup:boolean;csrfToken:string;user:null|{accountId:string;displayName:string;role:string;permissions:string[]}}
export interface EventRow {id:number;atUtcMs:number;category:string;actorName:string;tankId:string|null;tankCode:string|null;content:string;result:string;details:string}
let csrf='';
function issueSummary(issues: {field?:string;message:string}[], config?: Configuration) {
 const messages=issues.map(issue=>{
  const [section,id]=issue.field?.split('/')||[];
  const tank=section==='tanks' && config?.tanks?.find(t=>t.id===id);
  return tank ? tank.code+'：'+issue.message : issue.message;
 });
 return [...new Set(messages)].join('；');
}
export async function api<T>(url:string,method='GET',body?:unknown):Promise<T>{const response=await fetch(url,{method,credentials:'same-origin',headers:{'Content-Type':'application/json',...(method==='GET'?{}:{'X-CSRF-TOKEN':csrf})},...(body===undefined?{}:{body:JSON.stringify(body)})});const text=await response.text();let data:any={};try{data=text?JSON.parse(text):{}}catch{throw Error('服务响应格式异常')};if(response.status===401&&url!=='/api/auth/login')window.dispatchEvent(new Event('level-session-expired'));if(!response.ok)throw Error((data.error||`请求未成功（${response.status}）`)+(data.issues?'：'+issueSummary(data.issues, url==='/api/config' ? body as Configuration : url==='/api/storage/retention-change' ? (body as {configuration:Configuration})?.configuration : undefined):''));return data as T}
export async function session(){const value=await api<Session>('/api/session');csrf=value.csrfToken;currentPermissions.value=value.user?.permissions||[];return value}
declare global {interface Window{LocalWorkbook:{download:(name:string,sheets:{name:string;rows:unknown[][]}[])=>void}}}

export interface Metric {value:number|null;quality:'Good'|'Missing'|'Offline'|'Expired'|'Fault'|'Unconfigured';reason?:string;isValid:boolean}
export interface LiveReading {waterVolumeM3?:Metric;lastMeasuredLevelMm?:number|null;lastMeasuredLevelAtUtc?:string|null;frozenSurfaceAtUtc?:string|null;instrumentFaults:{displayCode:string|null;code:string;bit:number;description:string;firstDetectedAtUtc:string}[];faultTrackingPersisted:boolean;floatPositionMm:Metric;surfaceBasis:string;averageTaskId:string|null;tankId:string;configurationRevision:number;acquisitionSessionId:string;receivedAtUtc:string;sourceAtUtc:string|null;sourceTimeConfirmed:boolean;averageCompletedAtUtc:string|null;levelMm:Metric;instantTemperatureC:Metric;averageTemperatureC:Metric;instrumentDensityKgM3:Metric;atsPoints:{index:number;temperatureC:Metric}[];instrumentStatus:number|null;instrumentFault:number|null;diagnostic:string;currentVolumeM3:Metric;netVolumeM3:Metric;massT:Metric;calculationRevision:number;calculationNotes:string[];pressure1KPa:Metric;pressure2KPa:Metric;analog1Ma:Metric;analog2Ma:Metric}
export interface Acquisition {serverAtUtc:string;running:boolean;sessionId:string;configurationRevision:number;readings:LiveReading[];channels:{channelId:string;name:string;connected:boolean;completedCycles:number;lastCycleMs:number;lastCycleAtUtc:string|null;error:string}[];historyError:string}
export interface ArchivedReading {interfaceMm:Metric;tankId:string;atUtcMs:number;configurationRevision:number;levelMm:Metric;instantTemperatureC:Metric;averageTemperatureC:Metric;instrumentDensityKgM3:Metric;currentVolumeM3:Metric;netVolumeM3:Metric;massT:Metric;pressure1KPa:Metric;pressure2KPa:Metric;calculationRevision:number;atsPoints:{index:number;temperatureC:Metric}[]}
export interface HistoryPage {rows:ArchivedReading[];next:null|{atUtcMs:number;tankKey:number}}

export interface MeteringSettings {enabled:boolean;mainTableId:string|null;staticPressureTableId:string|null;decimalTableId:string|null;waterVolumeM3:number;waterHeightMm?:number|null;standardDensity20KgM3:number|null;manualOperatingDensityKgM3:number|null;densitySource:"Instrument"|"Manual";showInstrumentDensity:boolean;baseVolumeMethod:"Interpolate"|"MainPlusDecimal";fractionalMillimetreRule:"Unconfirmed"|"Linear"|"IntegerOnly"}
export interface CapacityDocument {id:string;revision:number;number:string;name:string;kind:'Main'|'StaticPressure'|'Decimal';heightUnit:'Millimetre'|'Centimetre'|'Metre';volumeUnit:'CubicMetre'|'Litre';referenceDensityKgM3:number;enabled:boolean;notes:string;points:{height:number;volume:number}[];decimalRows:DecimalCapacityRow[]}
export interface DecimalCapacityRow {startHeightMm:number;endHeightMm:number;number:number;centimetreLitres:number;millimetreLitres:number}
export interface CapacitySummary {id:string;revision:number;number:string;name:string;kind:CapacityDocument['kind'];heightUnit:CapacityDocument['heightUnit'];volumeUnit:CapacityDocument['volumeUnit'];referenceDensityKgM3:number;enabled:boolean;pointCount:number;associatedTanks:string[]}
export async function upload<T>(url:string,file:File):Promise<T>{const response=await fetch(url,{method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/octet-stream','X-CSRF-TOKEN':csrf},body:file});const data=await response.json();if(!response.ok)throw Error(data.error||'文件预览失败');return data as T}

export interface PressureCalibration {enabled:boolean;at4Ma:number;at20Ma:number;unit:'KPa'|'MPa'|'Bar'}
export interface DisplaySettings {refreshIntervalSeconds:number;heightUnit:'Millimetre'|'Centimetre'|'Metre';volumeUnit:'CubicMetre'|'Litre';densityUnit:'KilogramPerCubicMetre'|'GramPerCubicCentimetre';massUnit:'Tonne'|'Kilogram';volumeDecimals:number;massDecimals:number}
export interface TankGroup {id:string;name:string;kind:'Zone'|'TankGroup'|'Medium'|'Batch';parentId:string|null;tankIds:string[]}
export interface MediumDefinition {id:string;code:string;name:string;category:string;color:string;referenceTemperatureC:number|null;standardDensityKgM3:number|null}

export interface AlarmLimits {lowLow:number|null;low:number|null;high:number|null;highHigh:number|null;hysteresis:number;delaySeconds:number}
export interface AlarmSettings {level:AlarmLimits;temperature:AlarmLimits;density:AlarmLimits;pressure1:AlarmLimits;pressure2:AlarmLimits;communicationEnabled:boolean;instrumentEnabled:boolean}
export interface AlarmOccurrence {id:string;rule:{tankId:string;tankCode:string;key:string;name:string;quantity:string;threshold:number;severity:string};detectedAtUtc:string;activatedAtUtc:string;triggerValue:number|null;acknowledgedAtUtc:string|null;acknowledgedByName:string|null;restoredAtUtc:string|null;restoreValue:number|null;endedAtUtc:string|null;endReason:string|null;lastQuality:string;active:boolean;state:string}
export interface AlarmSummary {active:number;unacknowledged:number;recoveredUnacknowledged:number;tanks:{tankId:string;active:number;recoveredUnacknowledged:number;unacknowledged:number}[]}

export type TrendQuantity='Level'|'InstantTemperature'|'AverageTemperature'|'Density'|'CurrentVolume'|'NetVolume'|'Mass'|'Pressure1'|'Pressure2';
export interface TrendPoint {atUtcMs:number;value:number|null;quality:Metric['quality'];gapMarker:boolean}
export interface TrendResult {startUtcMs:number;endUtcMs:number;serverAtUtcMs:number;series:{selection:{tankId:string;quantity:TrendQuantity};points:TrendPoint[];sourceRows:number;reduced:boolean}[];timeBasis:string}

export interface DataJob {id:string;kind:'HistoryExport'|'Snapshot'|'DatabaseBackup'|'AverageTemperatureDispatch';state:'Queued'|'Running'|'Completed'|'Failed'|'Cancelled'|'Interrupted';actorId:string;actorName:string;createdAtUtc:string;rows:number;fileName:string|null;error:string|null;snapshotId:string|null}
export interface DataSnapshot {id:string;name:string;atUtc:string;configurationRevision:number;actorName:string;readings:LiveReading[]}

export interface DataSnapshotSummary {id:string;name:string;atUtc:string;configurationRevision:number;actorName:string;rows:number;validLevels:number}

export interface SnapshotSchedule {kind:'Snapshot'|'AverageTemperature';points:number;automaticOperationConfirmed:boolean;authorizedById:string|null;authorizedByName:string|null;authorizedSecurityVersion:number;id:string;name:string;time:string;enabled:boolean;tankIds:string[]|null}

export type InstrumentCommand='MeasureI'|'MeasureII'|'MeasureIII'|'Stop'|'Hoist'|'Lower'|'Density'|'Repeatability'|'OscillationAvoidance'|'RemoteLevel';
export interface ControlSettings {allowedCommands:InstrumentCommand[];wirelessMeasurementEnabled:boolean;confirmationTimeoutSeconds:number;movementTimeoutSeconds:number}
export interface ControlRecord {targetLevelMm:number|null;adjustmentReason:string|null;levelAdjustment:{targetLevelMm:number;currentLevelMm:number|null;previousOffsetMm:number|null;newOffsetMm:number|null;readbackOffsetMm:number|null;authorizationClearConfirmed:boolean|null;gatewayState:number|null;gatewayError:number|null}|null;id:string;command:InstrumentCommand|null;cycle:'Normal'|'IndependentStop';state:'Prepared'|'Queued'|'Checking'|'WriteIntent'|'Submitted'|'ReadbackConfirmed'|'Rejected'|'Unknown'|'Reviewed';actorId:string;actorName:string;createdAtUtc:string;writeIntentAtUtc:string|null;result:string;requiresReview:boolean;isReset:boolean;isAutomaticReset:boolean;relatedControlId:string|null;target:{tank:Tank;channel:Channel;configurationRevision:number};reviewReason:string|null}

export type WirelessStage='Queued'|'Checking'|'Authorizing'|'EnteringMeasureI'|'InitialStability'|'InitialDwell'|'Planning'|'Descending'|'Stopping'|'Settling'|'PointDwell'|'ReadingTemperature'|'RestoringMeasureI'|'Completed'|'Skipped'|'Failed'|'Cancelled'|'RequiresReview'|'Reviewed';
export interface WirelessPoint {number:number;targetMm:number;stoppedPositionMm:number|null;dwellStartedAtUtc:string|null;sampledAtUtc:string|null;temperature:Metric;attempts:number;rawTemperature:number|null;dwellElapsedSeconds:number}
export interface WirelessTaskRecord {id:string;version:number;target:{tank:Tank;channel:Channel;configurationRevision:number};actorName:string;createdAtUtc:string;finishedAtUtc:string|null;stage:WirelessStage;displayDueAtUtc:string|null;requestedPoints:number;ruleVersion:string;plan:{surfaceMm:number;plannedPoints:number;requiredValidPoints:number;targetsMm:number[]}|null;planFrozenAtUtc:string|null;points:WirelessPoint[];currentPoint:number;validPoints:number;baselineTemperature:Metric;averageTemperature:Metric;measureIRestored:boolean;holdsTank:boolean;hasSuccessfulResult:boolean;message:string;reviewReason:string|null}

export interface AuthorizationStatus {state:"Unlicensed"|"Active"|"Expired"|"ClockError"|"Invalid";effectiveUtc:string;expiresAtUtc:string|null;reminderDays:number;message:string;allowsLicensedActions:boolean}
export interface ProductPresentation {branding:{softwareName:string;manufacturerName:string;manufacturerInformation:string;logoDataUrl:string|null;iconDataUrl:string|null};enabledModules:string[]}

export interface GatewayControlChannel {value:number;state:number;error:number;count:number;canReset:boolean}
export interface GatewayControlSnapshot {configurationRevision:number;acquisitionSessionId:string;gatewayBootId:string;gatewayBindingVersion:number;observedAtUtc:string;normal:GatewayControlChannel;independentStop:GatewayControlChannel}
