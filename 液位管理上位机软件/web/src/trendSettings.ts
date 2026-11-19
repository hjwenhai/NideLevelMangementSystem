import type {Tank,TrendAxisSetting,TrendQuantity} from './api';
import {minimumAxisBounds} from './trendAxis';
export const trendNames:Record<TrendQuantity,string>={Level:'液位',InstantTemperature:'温度',AverageTemperature:'平均温度',Density:'仪表密度',CurrentVolume:'当前体积',NetVolume:'净体积',Mass:'质量',Pressure1:'压力输入1',Pressure2:'压力输入2'};
export function axisDefaults(q:TrendQuantity,tank?:Tank):TrendAxisSetting{
 const capacity=tank?.nominalCapacityM3||3000,density=tank?.metering.manualOperatingDensityKgM3||800;
 const span=({Level:100,InstantTemperature:5,AverageTemperature:5,Density:20,CurrentVolume:Math.max(1,capacity*.01),NetVolume:Math.max(1,capacity*.01),Mass:Math.max(1,capacity*density/100000),Pressure1:10,Pressure2:10})[q];
 return {mode:'Auto',minimumSpan:span,minimum:0,maximum:q==='Level'?(tank?.heightMm||12000):q==='Density'?1200:q==='CurrentVolume'||q==='NetVolume'?capacity:q==='Mass'?capacity*density/1000:100};
}
export function tankAxes(tank?:Tank):Record<TrendQuantity,TrendAxisSetting>{return Object.fromEntries((Object.keys(trendNames) as TrendQuantity[]).map(q=>[q,{...axisDefaults(q,tank),...(tank?.trends.axes?.[q]||{})}])) as Record<TrendQuantity,TrendAxisSetting>}
export function axisBounds(setting:TrendAxisSetting,values:number[],factor=1):{min?:number;max?:number}{
 if(setting.mode==='Fixed'&&setting.minimum!==null&&setting.maximum!==null&&setting.maximum>setting.minimum)return {min:setting.minimum*factor,max:setting.maximum*factor};
 return minimumAxisBounds(values,Math.max(0,setting.minimumSpan)*factor);
}
