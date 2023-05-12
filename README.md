# Load Test
## Running Victoria Metrics

https://github.com/VictoriaMetrics/VictoriaMetrics/tree/master/deployment/docker#victoriametrics-cluster

http://localhost:3000/dashboards

### Prometheus config

In file deployment/docker/prometheus-cluster.yml:

```yaml
- job_name: 'superapp'
  static_configs:
    - targets: ['host.docker.internal:5000']
```

```sh
make docker-cluster-up
```
## Running mysql container

```sh
docker run --name uuids-mysql-1 -e MYSQL_ROOT_PASSWORD=root -p13306:3306 -d mysql:8
docker run --name uuids-mysql-2 -e MYSQL_ROOT_PASSWORD=root -p23306:3306 -d mysql:8
docker run --name uuids-mysql-3 -e MYSQL_ROOT_PASSWORD=root -p33306:3306 -d mysql:8
```

Connection string to mysql
```connectionstring
Data Source=localhost; Port=13306; Character Set=utf8mb4; User Id=root; Password=root; convertzerodatetime=true; Allow User Variables=True; Pooling=true; Max Pool Size=50;SSL Mode=Preferred; ConnectionIdleTimeout=120; CancellationTimeout=-1; ConnectionTimeout=10; DefaultCommandTimeout=20; Keepalive=10; ServerRedirectionMode=Preferred;

Data Source=localhost; Port=23306; Character Set=utf8mb4; User Id=root; Password=root; convertzerodatetime=true; Allow User Variables=True; Pooling=true; Max Pool Size=50;SSL Mode=Preferred; ConnectionIdleTimeout=120; CancellationTimeout=-1; ConnectionTimeout=10; DefaultCommandTimeout=20; Keepalive=10; ServerRedirectionMode=Preferred;

Data Source=localhost; Port=33306; Character Set=utf8mb4; User Id=root; Password=root; convertzerodatetime=true; Allow User Variables=True; Pooling=true; Max Pool Size=50;SSL Mode=Preferred; ConnectionIdleTimeout=120; CancellationTimeout=-1; ConnectionTimeout=10; DefaultCommandTimeout=20; Keepalive=10; ServerRedirectionMode=Preferred;
```

Here's a little experiment. We insert and read the last N last records continuously. The table key is binary(16). The performance of write and read operations is compared for sequential uuids and the same data serialized to string and deserialized as a Guid. That is, Uuid and Guid have the same string representation, but in binary form the representation is different. In Guid, the bits that are essential for performance are rearranged. We see that the insert is slower by ~ 45% percent, the reading is slower by ~ 35%.

If you use the Guid.NewGuid() method the things get worse, as the bits are not just rearranged, they are generated in a manner that makes indexing algorithms helpless.

In my tests insertion slow down is 15-fold at one million keys and this is not the limit and reading is timing out.

What is the conclusion? A monotonic index works well for inserting and reading the latest timestamp data. If you break the monotony a little, so that it becomes piecewise monotonous, degradation occurs. If the monotony is broken strongly (randomly), then it will not be possible to work effectively with such data, degradation by several orders of magnitude and denial of service. The hypothesis is this - when inserting random keys, too many pages are affected, which become impossible to cache in memory, and the buffer pool is constantly flushed in mysql server.
