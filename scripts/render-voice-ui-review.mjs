// Review fixture only. Actual PNG assets and production coordinates; approximate
// panel/button artwork and heading font. Does not launch Valheim or capture input.
import { chromium } from '../web/node_modules/@playwright/test/index.mjs';
import { resolve } from 'node:path';
import { mkdir, readFile } from 'node:fs/promises';
const output = resolve(process.argv[2] ?? 'artifacts/voice-ui-review.png');
await mkdir(resolve(output, '..'), { recursive: true });
const pngs = await Promise.all([0, 1, 2, 3].map(async i => 'data:image/png;base64,' + (await readFile(new URL(`../plugins/ValheimServerManager.Client/Assets/Voice/microphone-${i}.png`, import.meta.url))).toString('base64')));
const browser = await chromium.launch({ executablePath: process.env.VSM_TEST_BROWSER });
try {
  const page = await browser.newPage({ viewport: { width: 1260, height: 1010 }, deviceScaleFactor: 1 });
  await page.setContent('<canvas width="1260" height="1010"></canvas><style>body{margin:0}</style>');
  await page.evaluate(async pngs => {
    const ctx = document.querySelector('canvas').getContext('2d');
    const icons = await Promise.all(pngs.map(src => new Promise(resolve => { const image = new Image(); image.onload = () => resolve(image); image.src = src; })));
    const text = (s, x, y, size = 20, color = '#ede6d9', heading = false) => {
      ctx.fillStyle = color; ctx.font = `${size}px ${heading ? 'Georgia' : 'Arial'}`; ctx.textAlign = 'left'; ctx.textBaseline = 'middle'; ctx.fillText(s, x, y);
    };
    const box = (x, y, w, h, fill, border) => { ctx.fillStyle = fill; ctx.fillRect(x,y,w,h); if(border){ctx.strokeStyle=border;ctx.lineWidth=1;ctx.strokeRect(x+.5,y+.5,w-1,h-1);} };
    box(0,0,1260,1010,'#122024');
    text('Voice rework · review fixture', 40, 42, 30, '#f5d08a', true);
    text('PNG pixels and layout only · not an in-game screenshot', 40, 80, 18, '#b5c3c4');
    text('HUD appears only while speaking', 40, 150, 24, '#f5d08a', true);
    ['Muted: hidden', 'Idle: hidden', 'Soft speech', 'Medium speech', 'Loud speech'].forEach((label, index) => {
      const y = 200 + index * 76;
      box(40, y - 27, 460, 60, index % 2 ? '#dae0d5' : '#1b3034');
      text(label, 62, y + 3, 18, index % 2 ? '#172327' : '#ede6d9');
      if (index >= 2) ctx.drawImage(icons[index - 1], 419, y - 21, 56, 44);
    });
    text('Input level is measured from captured samples.',40,630,18);
    text('Wave count: 1 / 2 / 3, with smoothing and hysteresis.',40,660,17);
    text('Push to talk keeps capture closed until the key is held.',40,705,17);
    text('A queued frame does not prove remote delivery.',40,735,17);
    text('The relay never echoes your voice back to you.',40,765,17);
    text('Hardware audio and native UI acceptance remain pending.',40,810,17,'#c3bba8');
    const nx=590, ny=124;
    box(nx,ny,620,814,'#252a29','#74664a');
    const label=(s,x,y,w,h,size=20,color='#ede6d9',heading=false)=>text(s,nx+x,ny+y+h/2,size,color,heading);
    const button=(s,x,y,w=556)=>{box(nx+x,ny+y,w,42,'#343c3c','#8b7958');text(s,nx+x+14,ny+y+21,20);};
    label('Voice chat',32,20,490,44,32,'#f5d08a',true);ctx.drawImage(icons[2],nx+536,ny+22,52,42);
    label('Voice ready · another eligible player must be nearby',32,66,556,40,16);
    button('Voice chat: Enabled',32,114);
    label('Voice mode',32,166,180,40);button('Push to talk  ›',228,166,360);
    label('Hold your key to send. The input meter follows capture.',32,210,556,28,16);
    label('Microphone',32,246,556,24);button('System default  ›',32,274);
    label('Select to cycle through available microphones.',32,320,556,24,15);
    label('Input level',32,350,556,22,15);
    box(nx+32,ny+376,556,10,'#121a1d');box(nx+32,ny+376,278,10,'#80cfa7');box(nx+65,ny+374,2,14,'#f5d08a');
    label('Input samples captured',32,388,556,40,15);
    label('Push to talk key',32,432,180,40);button('LeftAlt · Change',228,432,360);
    const slider=(name,value,y,percent)=>{label(`${name}: ${value}`,32,y,556,24,20);box(nx+32,ny+y+36,556,6,'#121a1d');box(nx+32,ny+y+36,556*percent,6,'#c8b271');box(nx+32+536*percent,ny+y+28,22,22,'#d2c390','#f2df9c');};
    slider('Playback volume','1.0',482,.5);slider('Microphone gain','1.0',542,1/3);slider('Speech threshold','0.015',602,.07);
    label('Captured 42 · queued 42 · skipped 0 · received 38',32,662,556,20,14);
    label('Frames queued to server (delivery unconfirmed)',32,686,556,20,15);
    label('Voice decoded · output samples consumed',32,710,556,36,15);
    button('Close · F8 / Esc / B',170,756,280);
    text('Regular body font; Valheim heading font is used only for the title at runtime.',40,975,17,'#c3cccc');
  }, pngs);
  await page.screenshot({ path: output });
  console.log(output);
} finally { await browser.close(); }
