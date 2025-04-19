# cs2-multifix
A CS# plugin that allows mappers to utilize trigger_multiple for basevelocity boosts, gravity, and nojump by simply naming a trigger_multiple

## **Server Owners**: How to use
1.) install plugin to addons folder

2.) restart server or `css_plugins load cs2-multifix` in console

## **Mappers**: How to use
**Simply `name` the trigger_multiple using the convention below and it will run OnEndTouch**
- `boost_speed500_z250` -> Creates a 500 directional speed boost / 250z (up) boost for player
- `boost_speed-500` -> Creates a -500 speed reduction for player
- `gravity_time1_amount200` -> sets player sv_gravity to 200 for 1 second
- `nojump` -> Create a large trigger_multiple box where you don't want the player to be able to get jump units. It will activate onstarttouch, and deactivate onendtouch

### Notes
- This plugin respects filter_activators if they exist for the boost / existing multiple
### Future Builds
- Add host_timescale modification
- Add sv_accelerate modification
