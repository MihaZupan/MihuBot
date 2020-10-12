pwd

echo "Waiting 5 seconds for the parent process to exit ..."
sleep 5s

git pull
dotnet run -c Release -- "$@"