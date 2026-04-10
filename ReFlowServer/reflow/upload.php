<?php
declare(strict_types=1);

// Reads request body for upload speed testing and discards it.
header('Cache-Control: no-store, no-cache, must-revalidate, max-age=0');
header('Pragma: no-cache');

$in = fopen('php://input', 'rb');
if ($in === false) {
  http_response_code(400);
  echo "no input";
  exit;
}

$bufSize = 1024 * 256;
while (!feof($in)) {
  $data = fread($in, $bufSize);
  if ($data === false) break;
  if ($data === '') break;
}
fclose($in);

http_response_code(200);
echo "ok";
exit;

