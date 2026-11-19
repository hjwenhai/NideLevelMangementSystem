import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
export default defineConfig({define:{__AVERAGE_DELIVERY__:process.env.LEVEL_AVERAGE_DELIVERY!=='false'},plugins:[vue()],build:{outDir:'../src/LevelManager.Host/wwwroot',emptyOutDir:true,rollupOptions:{output:{manualChunks(id){if(id.includes('/zrender/'))return 'chart-renderer';if(id.includes('/echarts/'))return 'chart-engine'}}}},server:{proxy:{'/api':'http://127.0.0.1:8780'}}});
