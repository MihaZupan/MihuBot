pwd

echo "Waiting 5 seconds for the parent process to exit ..."
sleep 5s

git pull
dotnet build -c Release

dotnet MihuBot/bin/Release/netcoreapp3.1/MihuBot.dll "$@"