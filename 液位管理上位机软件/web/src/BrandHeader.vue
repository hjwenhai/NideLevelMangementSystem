<script setup lang="ts">
import type {ProductPresentation} from './api';
defineProps<{branding:ProductPresentation['branding']}>();
</script>
<template>
 <div class="brand-header">
  <img v-if="branding.logoDataUrl" class="manufacturer-logo" :src="branding.logoDataUrl" :alt="branding.manufacturerName?branding.manufacturerName+'标识':'厂家标识'">
  <span v-else class="brand-monogram" aria-hidden="true">N<i></i></span>
  <div class="brand-wordmark"><strong :title="branding.manufacturerName||branding.softwareName">{{branding.manufacturerName||branding.softwareName}}</strong><small>TANK MONITORING</small></div>
 </div>
</template>
