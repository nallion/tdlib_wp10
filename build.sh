#!/bin/bash

# генерация случайной строки из 10 символов
RANDOM_MSG=$(tr -dc A-Za-z0-9 </dev/urandom | head -c 10)

echo $RANDOM_MSG
git add -A
git commit -m "$RANDOM_MSG"
git push
