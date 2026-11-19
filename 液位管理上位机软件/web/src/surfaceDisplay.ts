import type {LiveReading} from './api';
import {instrumentMode} from './instrumentMode';

// Display-only retained surface; it never changes measurement quality or inventory inputs.
export function hasFrozenSurface(reading:LiveReading|undefined,valid:boolean,protocol:string|undefined){
 if(!valid||!reading||reading.levelMm.isValid||reading.lastMeasuredLevelMm==null||reading.instrumentStatus==null||!reading.floatPositionMm.isValid)return false;
 const mode=instrumentMode(reading,true,protocol);
 return mode!=='测量Ⅰ'&&!mode.startsWith('未知');
}
