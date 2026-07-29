# Blockers

Источники: обсуждение upstream [Console compatibility #5727](https://github.com/space-wizards/RobustToolbox/discussions/5727), архитектура Robust/SS14.

## 1. Reflection + runtime codegen (критично)

Robust использует reflection и runtime code generation для:

- IoC-контейнера
- prototype / DataDefinition serialization
- network serialization

На Android типичный publish path — **AOT + trimming**. Reflection/codegen там либо запрещены, либо требуют огромных root descriptors и всё равно ломаются.

**Направление:** следить за AOT-работой upstream; параллельно фиксировать конкретные API, которые падают в наших probe; по возможности source generators вместо runtime emit.

## 2. Нативный десктопный клиентский стек

`Robust.Client` ожидает десктоп:

- окно (GLFW/SDL-подобные пути)
- мышь/клавиатура
- файловая система ПК
- OpenGL desktop context

Android = Activity lifecycle, touch, scoped storage, GLES/Vulkan.

**Направление:** новый platform backend, не «запустить exe в эмуляторе Windows».

## 3. Размер и контент

Клиент качает engine build + content packs сервера. На телефоне:

- место на диске
- время первой загрузки
- RAM (станция + PVS)

Observe легче полного геймплея, но всё равно тяжёлый.

## 4. Ввод и UX

SS14 UI рассчитан на мышь/клаву. Touch-маппинг — отдельный большой UX-проект даже после порта движка.

## 5. Юридическое / upstream

Форк движка ок (лицензии Robust/SS14 нужно соблюдать — см. `legal.md` upstream).  
Вливать Android-хаки в upstream можно только если они не ломают десктоп и согласованы с maintainers.

## Что НЕ является блокером прямо сейчас

- «Нет Java/Node» для Capacitor — для этого R&D нужен .NET Android (уже есть).
- Сайт `token_site` — не часть игрового клиента.
