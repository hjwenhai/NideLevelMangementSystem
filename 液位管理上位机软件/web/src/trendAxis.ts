// Bounds change only the drawing scale; samples and quality remain unchanged.
export function minimumAxisBounds(values:number[],minimumSpan:number):{min?:number;max?:number}{
 const good=values.filter(Number.isFinite);if(!good.length||minimumSpan<=0)return {};
 const lo=Math.min(...good),hi=Math.max(...good);if(hi-lo>=minimumSpan)return {};
 const middle=(lo+hi)/2;return {min:middle-minimumSpan/2,max:middle+minimumSpan/2};
}
