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
Data Source=localhost; Port=3306; Character Set=utf8mb4; User Id=root; Password=root; convertzerodatetime=true; Allow User Variables=True; Pooling=true; Max Pool Size=50;SSL Mode=Preferred; ConnectionIdleTimeout=120; CancellationTimeout=-1; ConnectionTimeout=10; DefaultCommandTimeout=20; Keepalive=10; ServerRedirectionMode=Preferred;
```

Тут небольшое исследование. Делаем табличку, и вставляем данные, одновременно постоянно читаем N последних записей. Ключ таблички - binary(16). Сравнивается производительность операций записи и чтения для Uuid.MySqlOptimized() и его же, приведенного к Guid через строку. То есть у Uuid и Guid одинаковое строковое представление, но в бинарном виде, и, соответственно, в ключе базы, представление разное. У Guid получаются переставлены местами существенные для производительности разряды. Видим, что вставка медленнее на ~45% процентов, чтение медленнее на ~35%.

Если использовать Guid.NewGuid() наблюдается замедление вставки в 15 раз, и это не предел. Я потом убрал даже это из теста, потому что происходят таймауты банальные.

Какой вывод? Монотонный индекс хорошо работает для вставки и чтения последних данных по таймстампу. Если немножко поломать монотонность, так что она станет кусочно-монотонной, происходит деградация. Если поломать монотонность сильно (рандом), то не получится эффективно работать с такими данными, деградация на несколько порядков и отказ в обслуживании. Гипотеза такая - при вставке рандомных ключей оказываются затронутыми слишком много страниц, которые становится невозможно кешировать в памяти, и buffer pool постоянно флашится.
