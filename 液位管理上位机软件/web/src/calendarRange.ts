export type CalendarPeriod='day'|'week'|'month';
// Follow Windows/browser local time. Calendar arithmetic handles Monday, month/year
// boundaries and daylight-saving changes; it is not a rolling 24/7/30-day window.
export function calendarRange(period:CalendarPeriod,now=new Date()):{start:Date;end:Date}{
 const start=new Date(now);start.setHours(0,0,0,0);
 if(period==='week')start.setDate(start.getDate()-(start.getDay()+6)%7);
 if(period==='month')start.setDate(1);
 return {start,end:new Date(now)};
}
