"""End-to-end HTTP checks. Run from repository root; requires Python 3 and .NET 10."""
import json, urllib.request, urllib.error, subprocess, tempfile, os, time, shutil
from pathlib import Path
root=Path(__file__).resolve().parents[1]
dotnet=os.environ.get('SMARTX_DOTNET','dotnet')
port=5089
base=f'http://127.0.0.1:{port}'
data=tempfile.mkdtemp(prefix='smartx-http-')
env=dict(os.environ,DataDirectory=data)
log=open(os.path.join(data,'server.log'),'w')
process=subprocess.Popen([dotnet,'run','--project',str(root/'src/SmartX.Api'),'-c','Release','--no-build','--urls',base],env=env,stdout=log,stderr=log)
def request(path,body=None,expected=200,raw=None,content_type=None):
 payload=raw if raw is not None else json.dumps(body).encode() if body is not None else None
 req=urllib.request.Request(base+path,data=payload,headers={'Content-Type':content_type or 'application/json'})
 try:
  with urllib.request.urlopen(req) as response:status=response.status;out=response.read()
 except urllib.error.HTTPError as e:status=e.code;out=e.read()
 assert status==expected,(path,status,out[:500])
 try:return json.loads(out)
 except:return out
try:
 for i in range(100):
  try:request('/api/health');break
  except (OSError,AssertionError):time.sleep(.1)
 else:raise RuntimeError('Server failed to start')
 assert b'SMART-X' in request('/')
 s=request('/api/summary');assert s['sensors']==1000 and s['accepted']==120000
 sensors=request('/api/sensors?take=3');now=lambda:time.strftime('%Y-%m-%dT%H:%M:%S',time.gmtime())+'.%06dZ'%int(time.time()%1*1000000)
 for sensor,kind,value in zip(sensors,['environmental','power','actuator'],[55.5,700,False]):
  packet={'sensorId':sensor['id'],'timestamp':now(),'value':value}
  request('/api/telemetry/'+kind,packet,202)
  assert request('/api/telemetry/'+kind+'/'+sensor['id'])[-1]['value']==value
  request('/api/telemetry/'+kind,packet,409)
 request('/api/telemetry/power',{'sensorId':sensors[1]['id'],'timestamp':now(),'value':5.5},400)
 request('/api/telemetry/actuator',{'sensorId':sensors[2]['id'],'timestamp':now(),'value':'false'},400)
 request('/api/telemetry/actuator',{'sensorId':sensors[2]['id'],'timestamp':now()},400)
 request('/api/telemetry/environmental',{'sensorId':sensors[0]['id'],'timestamp':now(),'value':101},400)
 request('/api/sensors',{'identifier':'BAD-ZONE','category':'Actuator','path':['Facility A','Maintenance'],'expectedIntervalSeconds':1},400)
 sensor=request('/api/sensors',{'identifier':'HTTP-CHECK','category':'Environmental','path':['Facility A','Utility Room'],'expectedIntervalSeconds':1},201)
 id=sensor['id']
 request('/api/simulator/'+id+'/sample',{'spike':True})
 assert 'alert' in request('/api/sensors/'+id+'/health')['status']
 time.sleep(3.1)
 assert request('/api/sensors/'+id+'/health')['status']=='Disconnected'
 request('/api/simulator/'+id+'/sample',{'spike':False})
 request('/api/simulator/'+id+'/sample',{'spike':False})
 assert request('/api/sensors/'+id+'/health')['status']=='Healthy'
 boundary='SmartXBoundary';payload=(f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="device.json"\r\nContent-Type: application/json\r\n\r\n{{"interval":5}}\r\n--{boundary}--\r\n').encode()
 attachment=request('/api/sensors/'+id+'/attachments',raw=payload,content_type='multipart/form-data; boundary='+boundary)
 assert request('/api/sensors/'+id+'/attachments/'+attachment['id'])=={'interval':5}
 load=request('/api/simulator/load',{'rounds':100});assert load['packets']==100100
 print('PASS HTTP: landing, 120,000 seeds, all typed endpoints, wrong types, required value, duplicate time, range validation, recursive registration, spike, timeout, recovery, upload/download and 100,100 packet load.')
 print(json.dumps(load))
finally:
 process.terminate();process.wait(timeout=15);log.close();shutil.rmtree(data)
