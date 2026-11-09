import json,struct,numpy as np
CT={5126:np.float32,5123:np.uint16,5125:np.uint32,5121:np.uint8}
NC={'SCALAR':1,'VEC2':2,'VEC3':3,'VEC4':4,'MAT4':16}
def load(p):
    d=open(p,'rb').read()
    l=struct.unpack('<I',d[12:16])[0]; j=json.loads(d[20:20+l])
    o=20+l; bl=struct.unpack('<I',d[o:o+4])[0]; b=d[o+8:o+8+bl]
    def acc(i):
        a=j['accessors'][i]; v=j['bufferViews'][a['bufferView']]
        off=v.get('byteOffset',0)+a.get('byteOffset',0); n=NC[a['type']]; t=CT[a['componentType']]
        st=v.get('byteStride'); isz=np.dtype(t).itemsize*n
        if st and st!=isz:
            raw=np.frombuffer(b,np.uint8,a['count']*st,off).reshape(a['count'],st)[:,:isz]
            return np.ascontiguousarray(raw).view(t).reshape(a['count'],n)
        return np.frombuffer(b,t,a['count']*n,off).reshape(a['count'],n) if n>1 else np.frombuffer(b,t,a['count'],off)
    def img(i):
        v=j['bufferViews'][j['images'][i]['bufferView']]; o2=v.get('byteOffset',0); return b[o2:o2+v['byteLength']]
    return j,acc,img
