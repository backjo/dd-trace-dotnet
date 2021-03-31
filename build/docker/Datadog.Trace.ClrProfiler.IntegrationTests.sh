#!/bin/bash
set -uxo pipefail

cd "$( dirname "${BASH_SOURCE[0]}" )"/../../

mkdir -p /var/log/datadog/dotnet
touch /var/log/datadog/dotnet/dotnet-tracer-native.log

#https://docs.microsoft.com/en-us/dotnet/core/diagnostics/dumps#collecting-dumps-on-crash
export COMPlus_DbgEnableMiniDump=1
export COMPlus_DbgMiniDumpType=4

dotnet vstest test/Datadog.Trace.IntegrationTests/bin/$buildConfiguration/$publishTargetFramework/publish/Datadog.Trace.IntegrationTests.dll --logger:trx --ResultsDirectory:test/Datadog.Trace.IntegrationTests/results
st01=$?

dotnet vstest test/Datadog.Trace.OpenTracing.IntegrationTests/bin/$buildConfiguration/$publishTargetFramework/publish/Datadog.Trace.OpenTracing.IntegrationTests.dll --logger:trx --ResultsDirectory:test/Datadog.Trace.OpenTracing.IntegrationTests/results
st02=$?

wait-for-it servicestackredis:6379 -- \
wait-for-it stackexchangeredis:6379 -- \
wait-for-it elasticsearch6:9200 -- \
wait-for-it elasticsearch5:9200 -- \
wait-for-it sqlserver:1433 -- \
wait-for-it mongo:27017 -- \
wait-for-it postgres:5432 -- \
dotnet vstest test/Datadog.Trace.ClrProfiler.IntegrationTests/bin/$buildConfiguration/$publishTargetFramework/publish/Datadog.Trace.ClrProfiler.IntegrationTests.dll --logger:trx --ResultsDirectory:test/Datadog.Trace.ClrProfiler.IntegrationTests/results
st03=$?

# Collect run data
mkdir /project/data
cp /var/log/datadog/dotnet/dotnet-tracer-native.log /project/data/
cp /tmp/coredump* /project/data/ 2>/dev/null || :
ls /project/data


[ st01 -eq 1 ] && exit st01
[ st02 -eq 1 ] && exit st02
[ st03 -eq 1 ] && exit st03