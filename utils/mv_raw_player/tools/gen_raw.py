import struct, sys, math
import random
W,H=640,480
dur_s=int(sys.argv[2]) if len(sys.argv)>2 else 6
out=sys.argv[1]
rng=random.Random(1)
words=[]
hdr=("% evt 2.0\n% format EVT2;height={};width={}\n% geometry {}x{}\n% end\n".format(H,W,W,H)).encode()
ev=[]
# moving ring: angle depends on time; ~40k events/s
step_us=25
t=0
end=dur_s*1_000_000
while t<end:
    a=2*math.pi*(t/1e6)/2.0
    cx=W/2+200*math.cos(a); cy=H/2+120*math.sin(a)
    th=rng.uniform(0,2*math.pi)
    r=30+rng.gauss(0,1.5)
    x=int(cx+r*math.cos(th)); y=int(cy+r*math.sin(th))
    if 0<=x<W and 0<=y<H:
        ev.append((t,x,y,1 if math.sin(th-a)>0 else 0))
    t+=step_us
last_hi=-1
with open(out,'wb') as f:
    f.write(hdr)
    buf=bytearray()
    for (t,x,y,p) in ev:
        hi=t>>6
        if hi!=last_hi:
            buf+=struct.pack('<I',(0x8<<28)|(hi&0x0FFFFFFF)); last_hi=hi
        buf+=struct.pack('<I',((1 if p else 0)<<28)|((t&0x3F)<<22)|(x<<11)|y)
    f.write(buf)
print(len(ev),'events',dur_s,'s')
