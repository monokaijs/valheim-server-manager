// Layout review only: actual PNGs and production dimensions, with approximate
// heading font. This neither launches Valheim nor captures an in-game UI.
import { chromium } from '../web/node_modules/@playwright/test/index.mjs';
import { resolve } from 'node:path';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
const output = resolve(process.argv[2] ?? 'artifacts/voice-ui-review.png');
await mkdir(resolve(output, '..'), { recursive: true });
const code = await readFile(new URL('../plugins/ValheimServerManager.Client/VoicePresentation.cs', import.meta.url),'utf8');
const [width,height] = /PanelWidth = ([\d.]+)f, PanelHeight = ([\d.]+)f/.exec(code).slice(1).map(Number);
const png = 'data:image/png;base64,' + (await readFile(new URL('../plugins/ValheimServerManager.Client/Assets/Voice/microphone-2.png', import.meta.url))).toString('base64');
const browser = await chromium.launch({ executablePath: process.env.VSM_TEST_BROWSER });
try {
 const page = await browser.newPage({viewport:{width:width+100,height:height+160},deviceScaleFactor:1});
 const html=`<!doctype html><meta charset="utf-8"><style>
 *{box-sizing:border-box}body{margin:0;background:#101b20;color:#edf5f2;font-family:Arial,sans-serif} .caption{position:absolute;left:50px;top:22px;font-size:20px} .sub{position:absolute;left:50px;top:52px;color:#9bafb7;font-size:14px}.panel{position:absolute;left:50px;top:102px;width:${width}px;height:${height}px;background:#131f24;border-top:2px solid #ffc766}.panel>*{position:absolute;margin:0}.muted{color:#a3babe}.button{height:42px;background:#1f2e33;border:0;color:#edf5f2;font:18px Arial;display:flex;align-items:center;justify-content:center}.heading{font:32px Georgia;color:#ffc766}.small{font-size:14px}.label{display:flex;align-items:center}.slider{height:44px;cursor:pointer}.track{position:absolute;left:12px;right:12px;top:18px;height:8px;background:#0e1a1f}.fill{height:8px;background:#91c4ad}.thumb{position:absolute;top:10px;width:24px;height:24px;transform:translateX(-50%);background:#ffc766;border:4px solid #ffc766;box-shadow:inset 0 0 0 8px #131f24}.focused .thumb{background:white;border-color:white}.value{text-align:right;color:#ffc766;font-size:20px}.range{font-size:12px;color:#a3babe;display:flex;justify-content:space-between}
 </style><div class="caption">Voice settings · after</div><div class="sub">Layout fixture with packaged PNG · not an in-game screenshot</div><div class="panel">
 <img style="left:30px;top:25px;width:42px;height:34px;object-fit:contain" src="${png}"><h1 class="heading" style="left:86px;top:20px;height:44px;line-height:44px">Voice chat</h1><button class="button" style="left:512px;top:24px;width:176px">Voice enabled</button>
 <p class="muted label" style="left:32px;top:70px;height:24px;font-size:17px">Nearby voices fade naturally with distance.</p>
 <div style="left:32px;top:106px;width:656px;height:40px;background:#1f2e33;padding:11px 12px;font-size:16px">Voice ready · another eligible player must be nearby</div>
 <label class="muted small" style="left:32px;top:160px">VOICE MODE</label><label class="muted small" style="left:372px;top:160px">PUSH TO TALK KEY</label>
 <button class="button" style="left:32px;top:184px;width:320px">Push to talk ›</button><button class="button" style="left:372px;top:184px;width:316px">LeftAlt · Change</button>
 <p class="muted" style="left:32px;top:232px;font-size:16px">Hold your key to send. The input meter follows capture.</p>
 <label class="muted small" style="left:32px;top:270px">MICROPHONE</label><button class="button" style="left:32px;top:294px;width:656px">System default ›</button>
 <p class="muted" style="left:32px;top:342px;font-size:15px">Select to cycle through available microphones.</p><p style="left:32px;top:370px;font-size:15px">Input level</p>
 <div style="left:32px;top:396px;width:656px;height:10px;background:#0e1a1f"><div style="width:40%;height:10px;background:#80cfa7"></div><i style="position:absolute;left:6%;top:-2px;width:2px;height:14px;background:#ffc766"></i></div>
 <p class="muted" style="left:32px;top:412px;font-size:15px">Input samples captured</p>
 ${[['Playback volume','100%',450,.5,'0%','200%'],['Microphone gain','1.00×',532,1/3,'0.00×','3.00×'],['Speech threshold','1.5% RMS',614,.014/.199,'0.1% RMS','20.0% RMS']].map(([name,value,y,t,min,max],i)=>`<label style="left:32px;top:${y}px;font-size:20px">${name}</label><output class="value" style="left:500px;top:${y}px;width:188px">${value}</output><div class="slider ${i===0?'focused':''}" data-value="${t}" style="left:32px;top:${y+28}px;width:656px"><div class="track"><div class="fill" style="width:${t*100}%"></div></div><div class="thumb" style="left:${12+632*t}px"></div></div><div class="range" style="left:32px;top:${y+68}px;width:656px"><span>${min}</span><span>${max}</span></div>`).join('')}
 <p class="muted small" style="left:32px;top:700px">Frames queued to server (delivery unconfirmed)</p><p class="muted small" style="left:32px;top:720px">Voice decoded · output samples consumed</p><p class="muted" style="left:32px;top:752px;font-size:13px">Sent 250 · received 250 · output gaps 0</p><button class="button" style="left:472px;top:756px;width:216px">Close · F8 / Esc / B</button></div>`;
 await page.setContent(html); await page.screenshot({path:output});
 const checks=await page.evaluate(()=>[...document.querySelectorAll('.slider')].map(s=>{
  const r=s.getBoundingClientRect(),t=s.querySelector('.thumb').getBoundingClientRect(),track=s.querySelector('.track').getBoundingClientRect(),fill=s.querySelector('.fill').getBoundingClientRect();
  return {hitHeight:r.height,thumbWidth:t.width,thumbHeight:t.height,aligned:Math.abs((t.left+t.width/2)-(fill.left+fill.width))<.1,contained:t.left>=r.left&&t.right<=r.right,trackHeight:track.height};
 }));
 if(checks.some(c=>c.hitHeight<44||c.thumbHeight!==24||!c.aligned||!c.contained)) throw Error('Review slider geometry invalid');
 await writeFile(output.replace(/\.png$/,'.json'),JSON.stringify({fixture:true,actualGame:false,width,height,sliders:checks},null,2));
 console.log(output);
} finally {await browser.close();}
