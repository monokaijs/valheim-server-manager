// Layout fixture only, not an in-game screenshot. Uses no game artwork/fonts.
// Run: node scripts/render-voice-ui-review.mjs [output.png]
import { chromium } from '../web/node_modules/@playwright/test/index.mjs';
import { resolve } from 'node:path';
import { mkdir } from 'node:fs/promises';
const output = resolve(process.argv[2] ?? 'artifacts/voice-ui-review.png');
await mkdir(resolve(output, '..'), { recursive: true });
const browser = await chromium.launch({ executablePath: process.env.VSM_TEST_BROWSER });
try {
  const page = await browser.newPage({ viewport: { width: 1480, height: 1040 }, deviceScaleFactor: 1 });
  await page.setContent('<canvas width="1480" height="1040"></canvas><style>body{margin:0}</style>');
  await page.evaluate(() => {
    const ctx = document.querySelector('canvas').getContext('2d');
    const text = (s, x, y, size = 20, color = '#e9e2d0', font = 'Georgia') => {
      ctx.fillStyle = color; ctx.font = `${size}px ${font}`; ctx.textAlign = 'left'; ctx.textBaseline = 'middle'; ctx.fillText(s, x, y);
    };
    const box = (x, y, w, h, fill, border) => { ctx.fillStyle = fill; ctx.fillRect(x,y,w,h); if(border){ctx.strokeStyle=border;ctx.lineWidth=2;ctx.strokeRect(x+1,y+1,w-2,h-2);} };
    box(0,0,1480,1040,'#121a1c');
    text('Companion voice UI', 48, 49, 32);
    text('BEFORE / AFTER · layout fixture, not an in-game capture',48,90,18,'#aaa','Arial');
    text('Before — default Unity IMGUI',48,141,22,'#b9b9b9','Arial');
    text('After — native Valheim resources at runtime',780,141,22,'#e8bd69');
    const states = ['Muted','Ready','Transmitting','Unavailable'];
    const distance=(x,y,cx,cy)=>Math.hypot(x-cx,y-cy);
    const segment=(x,y,ax,ay,bx,by,r)=>{const t=Math.max(0,Math.min(1,((x-ax)*(bx-ax)+(y-ay)*(by-ay))/((bx-ax)**2+(by-ay)**2)));return distance(x,y,ax+t*(bx-ax),ay+t*(by-ay))<=r;};
    // Same original silhouette geometry as VoiceIcon.cs (no copied game asset).
    function icon(state, x, y) {
      const temp=document.createElement('canvas');temp.width=temp.height=96;
      const tc=temp.getContext('2d'),p=tc.createImageData(96,96);
      for(let py=0;py<96;py++)for(let px=0;px<96;px++){
        const capsule=px>=34&&px<=54&&py>=18&&py<=49||distance(px,py,44,18)<=10||distance(px,py,44,49)<=10;
        const arc=py>=43&&py<=68&&Math.abs(distance(px,py,44,43)-24)<=2.5;
        const stand=px>=41&&px<=47&&py>=68&&py<=81||px>=30&&px<=58&&py>=79&&py<=84;
        const extra=state===0&&segment(px,py,14,16,76,84,4)||state===2&&(segment(px,py,76,30,83,37,2.5)||segment(px,py,83,37,83,55,2.5)||segment(px,py,83,55,76,62,2.5))||state===3&&(px>=76&&px<=82&&py>=21&&py<=48||distance(px,py,79,60)<=4);
        if(extra||(capsule||arc||stand)&&!(state===0&&segment(px,py,14,16,76,84,7))){const i=(py*96+px)*4;p.data[i]=state===2?255:242;p.data[i+1]=state===2?204:235;p.data[i+2]=state===2?89:212;p.data[i+3]=255;}
      }
      tc.putImageData(p,0,0);box(x-4,y-4,52,52,'#050505');ctx.drawImage(temp,x,y,44,44);
    }
    states.forEach((state,i)=>{icon(i,802+i*160,181);text(state,790+i*160,247,16,'#ddd','Arial');});
    box(48,179,650,98,'#26312e');box(487,198,165,28,'#555','#777');text('Voice transmitting',498,212,15,'white','Arial');
    const ox=102,oy=303;
    box(ox,oy,540,600,'#454545','#777');box(ox+2,oy+2,536,26,'#555');text('Server Manager · Voice chat',ox+159,oy+15,14,'#eee','Arial');
    const old=(s,x,y)=>text(s,ox+x,oy+y,15,'#eee','Arial');
    old('☑ Enable voice chat',20,57);old('Mode',20,92);
    ['Push to talk','Voice activation','Open mic'].forEach((s,i)=>{box(ox+20+i*165,oy+105,163,28,'#666','#777');old(s,35+i*165,119);});
    old('Microphone',20,158);
    ['● System default','Built-in microphone','USB microphone'].forEach((s,i)=>{box(ox+20,oy+175+i*34,500,29,'#666','#777');old(s,185,189+i*34);});
    old('Push to talk key',20,301);box(ox+20,oy+316,500,28,'#666','#777');old('LeftAlt · click to change',195,330);old('F8 opens or closes this panel.',20,365);
    ['Playback volume: 1.0','Microphone gain: 1.0','Voice activation threshold: 0.025'].forEach((s,i)=>{old(s,20,403+i*51);box(ox+20,oy+417+i*51,500,4,'#282828');box(ox+180+i*25,oy+410+i*51,14,18,'#999','#aaa');});
    box(ox+20,oy+552,500,32,'#666','#777');old('Close',249,568);
    const nx=780,ny=294;
    box(nx,ny,620,680,'#191b18','#7c6843');box(nx+6,ny+6,608,668,'#1c211d','#3f4936');
    // Geometry/text sizes match VoiceSettingsPanel.cs. Font and sprite approximations
    // deliberately remain labeled as a fixture; the plugin uses installed resources.
    const n=(s,x,y,size=20,color='#ece5d4')=>text(s,nx+x,ny+y,size,color);
    const button=(s,x,y,w)=>{box(nx+x,ny+y,w,42,'#352f22','#8e754a');ctx.textAlign='center';ctx.fillStyle='#eee5ce';ctx.font='21px Georgia';ctx.fillText(s,nx+x+w/2,ny+y+21);ctx.textAlign='left';};
    n('Voice chat',32,44,32,'#ffca59');n('Proximity voice · F8 / Esc / B to close',32,85,18);
    button('Voice chat: Enabled',32,110,556);n('Voice mode',32,190,22);button('Push to talk  ›',228,170,360);n('Microphone opens only while your key is held.',32,231,17);
    n('Microphone',32,264,22);button('System default  ›',32,280,556);n('Select to cycle through available microphones.',32,338,16);
    n('Push to talk key',32,380,22);button('LeftAlt · Change',228,360,360);
    ['Playback volume: 1.0','Microphone gain: 1.0','Activation threshold: 0.025'].forEach((s,i)=>{const y=420+i*62;n(s,32,y+12,20);box(nx+32,ny+y+36,556,6,'#373728','#675b3d');box(nx+32,ny+y+36,180+i*16,6,'#b99a52');box(nx+204+i*16,ny+y+28,22,22,'#c7b67d','#e6d297');});
    button('Close',200,618,220);
    text('Native sprites/font are loaded from the installed game; this fixture approximates their appearance.',48,988,17,'#aab6b5','Arial');
    text('Review target: compact icon states · native controls · keyboard/controller focus · safe cleanup',48,1016,17,'#aab6b5','Arial');
  });
  await page.screenshot({ path: output });
  console.log(output);
} finally { await browser.close(); }
