const http = require('http');

const hostname = '127.0.0.1';
const port = 3000;

var mc = require('minecraft-protocol');
var client = newClient();

const server = http.createServer((req, res) => {
  res.statusCode = 200;
  res.setHeader('Content-Type', 'text/plain');
  if (req.url.startsWith("/execute/")) {
    var command = "/" + decodeURI(req.url.substring(9));
    console.log("Got a command: " + command);
    client.write('chat', { message: command });
    res.end('Executed ' + command);
  }
  else {
    res.end('Hello World');
  }
});

server.listen(port, hostname, () => {
  console.log(`Server running at http://${hostname}:${port}/`);
})

process
  .on('unhandledRejection', (reason, p) => {
    console.error(reason, 'Unhandled Rejection at Promise', p);
  })
  .on('uncaughtException', err => {
    console.error(err, 'Uncaught Exception thrown');
    client = newClient();
  });

function newClient() {
  return mc.createClient({
    host: "darlings.me",
    port: 25565,
    username: "mihazupan.zupan1@gmail.com",
    password: "***REMOVED***",
  });
}