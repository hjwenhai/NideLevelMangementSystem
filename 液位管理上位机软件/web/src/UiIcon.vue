<script setup lang="ts">
defineProps<{name:string}>();
const paths:Record<string,string[]>={
 menu:['M4 6h16M4 12h16M4 18h16'],
 stop:['M6 6h12v12H6z'],
 download:['M12 3v12M7 10l5 5 5-5M4 16v5h16v-5'],
 overview:['M3 3h7v7H3zM14 3h7v7h-7zM3 14h7v7H3zM14 14h7v7h-7z'],
 list:['M9 5h12M9 12h12M9 19h12M3 5h1M3 12h1M3 19h1'],
 detail:['M6 5c0-3 12-3 12 0v14c0 3-12 3-12 0zM6 5c0 3 12 3 12 0M6 15c0 3 12 3 12 0'],
 history:['M3 4v16h18M6 15l4-5 4 3 6-8'],
 trends:['M3 4v16h18M5 14l5-7 4 9 6-12'],
 wireless:['M9 14V5a3 3 0 0 1 6 0v9a5 5 0 1 1-6 0zM12 8v9'],
 snapshots:['M4 6h4l2-3h4l2 3h4v14H4zM16 13a4 4 0 1 1-8 0 4 4 0 0 1 8 0'],
 alarms:['M6 9a6 6 0 0 1 12 0c0 7 3 7 3 8H3c0-1 3-1 3-8M10 21h4'],
 events:['M6 3h9l4 4v14H6zM14 3v5h5M9 12h7M9 16h7'],
 reports:['M5 3h14v18H5zM9 7h6M9 11h6M9 15h6'],
 tanks:['M5 4c0-2 14-2 14 0v16c0 2-14 2-14 0zM5 4c0 2 14 2 14 0M5 10h14M5 16h14'],
 channels:['M9 8H6a4 4 0 0 0 0 8h4M15 8h3a4 4 0 0 1 0 8h-4M8 12h8'],
 basics:['M4 4h16v16H4zM4 9h16M9 9v11'],
 groups:['M9 3h6v5H9zM3 16h6v5H3zM15 16h6v5h-6zM12 8v4M6 16v-4h12v4'],
 jobs:['M4 5h16v15H4zM8 2v5M16 2v5M4 10h16M8 14h3M8 17h6'],
 system:['M4 6h16M4 12h16M4 18h16M8 3v6M16 9v6M10 15v6'],
 pin:['m15 3 6 6-4 1-3 5-5-5 5-3zM9 15l-6 6'],
 check:['m5 12 4 4L19 6'],
 chevron:['m9 5 7 7-7 7']
};
</script>
<template><svg class="ui-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false"><path v-for="(d,i) in paths[name]||paths.basics" :key="i" :d="d"/></svg></template>
